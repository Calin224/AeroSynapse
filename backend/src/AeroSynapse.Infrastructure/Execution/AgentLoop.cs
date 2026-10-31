using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Management;
using AeroSynapse.Application.Operations;
using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.DigitalTwin;
using AeroSynapse.Infrastructure.Governance;
using AeroSynapse.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AeroSynapse.Infrastructure.Execution;

/// <summary>
/// Pauses a run at each decision point, shows its progress live, asks the model to pick one of the offered options and
/// records the decision; returns the run's request with every decision. Decisions already recorded for the run are
/// reused without asking again, so retrying or reconciling a run sends the same request.
/// </summary>
public sealed class AgentLoop(TwinDbContext db, IAgentEngine engine, IAgentModel model, IRunProgressRecorder progress,
    AiGovernance governance, IOptions<AgentOptions> options, TimeProvider clock, ILogger<AgentLoop> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Decision minutes: every interval while a move can still take effect, plus each scheduled delay.</summary>
    public static IReadOnlyList<(int Minute, Guid? EventId)> Points(ScenarioAgent agent, SimulationRequest request)
    {
        var points = new List<(int Minute, Guid? EventId)>();
        var gatesOpen = agent.GateChoices && request.Journey is not null;
        if (agent.StaffMoves || agent.CheckInDesks || agent.Reserve > 0 || gatesOpen)
            for (var minute = agent.DecisionIntervalMinutes; minute + 1 + agent.TransferMinutes < request.DurationMinutes; minute += agent.DecisionIntervalMinutes)
                points.Add((minute, null));
        if (gatesOpen)
        {
            var delays = (request.Events ?? []).Where(e => e.Type == "flight_change" && e.Minute > 0 && e.Minute < request.DurationMinutes).ToArray();
            points.AddRange(delays.Select(e => (e.Minute, (Guid?)e.EventId)));
            // A delay also changes what is queueing: look at the boarding queues right then, not only at the next interval.
            points.AddRange(delays.Where(e => e.Minute + 1 + agent.TransferMinutes < request.DurationMinutes).Select(e => (e.Minute, (Guid?)null)));
        }
        return points.Distinct().OrderBy(point => point.Minute).ThenBy(point => point.EventId is null ? 0 : 1).ThenBy(point => point.EventId).ToArray();
    }

    public async Task<SimulationRequest> PrepareAsync(SimulationRun run, RunInputSnapshot captured, SimulationRequest request, CancellationToken token)
    {
        var inputs = captured.Scenario.Inputs;
        if (inputs.Agent is not { } agent || captured.Engine != KpiConventions.Engine) return request;
        // Repeated tests and staffing evaluations compare fixed variants; the agent acts only in single runs.
        if (await db.BatchReplications.AsNoTracking().AnyAsync(item => item.RunId == run.Id, token)) return request;
        var layout = captured.Layout.Layout;
        // Codes (CP-1) are what the map, the diagnosis and the operators use.
        var names = layout.Checkpoints.ToDictionary(c => c.Id, c => string.IsNullOrWhiteSpace(c.Code) ? c.Name : c.Code);
        var deskCount = inputs.CheckIn is { } checkIn ? layout.CheckInAreas?.SingleOrDefault(area => area.Id == checkIn.AreaId)?.DeskCount : null;
        var frozen = (request.Events ?? []).Where(e => e.CheckpointId is not null).Select(e => e.CheckpointId!.Value).ToHashSet();
        var flights = inputs.Flights.ToDictionary(f => f.Id, f => f.Code);
        var gateTerminals = layout.Gates.ToDictionary(g => g.Id, g => g.TerminalId);
        var gateNames = layout.Gates.ToDictionary(g => g.Id, g => string.IsNullOrWhiteSpace(g.Code) ? g.Name : g.Code);
        var reserved = inputs.StaffAllocations.Select(item => item.StaffMemberId)
            .Concat(inputs.StaffShifts?.Shifts.Select(item => item.StaffMemberId) ?? [])
            .Concat(inputs.IntegratedResources?.Workers.Select(item => item.StaffMemberId) ?? []).ToHashSet();
        var recorded = await db.AgentDecisions.Where(d => d.RunId == run.Id).OrderBy(d => d.Sequence).ToListAsync(token);
        var chosen = recorded.Select(d => (d.Minute, Option: JsonSerializer.Deserialize<AgentOption>(d.ActionJson, Json)!)).ToList();
        var calls = recorded.Count(d => d.ModelStatus != "single_option");
        foreach (var (minute, eventId) in Points(agent, request))
        {
            var point = eventId?.ToString() ?? "periodic";
            if (recorded.Any(d => d.Minute == minute && d.Point == point)) continue;
            var current = AgentDecisionInputs.Apply(request, chosen, agent.TransferMinutes);
            var reserveLeft = agent.Reserve - chosen.Where(item => item.Option.Kind is "staff_call" or "plan" or "checkpoint_open").Sum(item => item.Option.Count ?? 0);
            var limits = new AgentLimits(agent.CheckInDesks ? deskCount : null, Math.Max(0, reserveLeft), agent.TransferMinutes, frozen.ToArray());
            var observation = await engine.ObserveAsync(current, minute, eventId, limits, token);
            await progress.RecordAsync(run.Id, new EngineProgress(minute, observation.Checkpoints
                .Select(c => new ProgressZone(c.CheckpointId, c.Queued, c.Processed, c.NotArrived)).ToArray()), token);
            var offered = eventId is null ? AgentCandidates.Periodic(observation, agent, frozen, names, deskCount, reserveLeft)
                : observation.Gate is { } gate ? AgentCandidates.Gate(gate) : [];
            if (eventId is null && agent.GateChoices && observation.Boarding is not null)
            {
                var used = chosen.Where(item => item.Option.Kind == "gate_open").Select(item => item.Option.StaffMemberId!.Value).ToHashSet();
                var pool = layout.Staff.Where(s => !reserved.Contains(s.Id) && !used.Contains(s.Id))
                    .Select(s => new AgentGateStaff(s.Id, s.TerminalId, string.IsNullOrWhiteSpace(s.DisplayName) ? s.Code : s.DisplayName)).ToArray();
                offered = [.. offered, .. AgentCandidates.GateOpenings(observation, pool, gateTerminals, gateNames, flights)];
                // Without staff, desks or reserve to offer, a point with nothing to open is not a decision.
                if (offered.Count == 1 && !agent.StaffMoves && !agent.CheckInDesks && agent.Reserve == 0) continue;
            }
            // Queue transfers stay within one terminal; donor operators must belong to that terminal too.
            var terminals = layout.Checkpoints.ToDictionary(c => c.Id, c => c.TerminalId);
            offered = offered.Where(o => o.Kind is not ("checkpoint_open" or "queue_redistribution")
                || o.FromCheckpointId is { } from && o.ToCheckpointId is { } to
                    && terminals.GetValueOrDefault(from) == terminals.GetValueOrDefault(to)
                    && (o.Staff ?? []).All(t => terminals.GetValueOrDefault(t.CheckpointId) == terminals.GetValueOrDefault(to))
                    && minute + 1 + agent.TransferMinutes + Math.Max(1, agent.TransferMinutes) < request.DurationMinutes).ToArray();
            if (offered.Count == 0) continue;
            var started = clock.GetTimestamp();
            var choice = offered.Count == 1 ? new AgentChoice("single_option", offered[0].Id, "", model.Status.Model)
                : ++calls > options.Value.MaxModelCallsPerRun ? new AgentChoice("budget_exhausted", null, null, model.Status.Model)
                : await AskAsync(run, captured, observation, offered, names, flights, deskCount, agent, token);
            // Without an answer the deterministic rule decides; the status still records why the model was silent.
            var picked = offered.FirstOrDefault(option => option.Id == choice.OptionId);
            if (picked is null)
            {
                (picked, var reason) = AgentCandidates.Fallback(observation, offered, names);
                choice = choice with { Reason = reason };
            }
            var decision = new AgentDecision
            {
                Id = Guid.NewGuid(), RunId = run.Id, ScenarioId = captured.Scenario.Scenario.Id, IsSynthetic = run.IsSynthetic,
                Sequence = recorded.Count + 1, Minute = minute, Point = point, Kind = picked.Kind, OptionId = picked.Id,
                ActionJson = JsonSerializer.Serialize(picked, Json), OptionsJson = JsonSerializer.Serialize(offered, Json),
                ObservationJson = JsonSerializer.Serialize(observation, Json), Reason = choice.Reason ?? "", ModelStatus = choice.Status,
                Model = choice.Model, InputTokens = choice.InputTokens, OutputTokens = choice.OutputTokens,
                DurationMs = clock.GetElapsedTime(started).TotalMilliseconds, RecordedAtUtc = clock.GetUtcNow().UtcDateTime
            };
            db.AgentDecisions.Add(decision);
            await db.SaveChangesAsync(token);
            recorded.Add(decision);
            chosen.Add((minute, picked));
            logger.LogInformation("Agent run={RunId} minute={Minute} point={Point} option={OptionId} status={Status}", run.Id, minute, point, picked.Id, choice.Status);
        }
        return AgentDecisionInputs.Apply(request, chosen, agent.TransferMinutes);
    }

    private async Task<AgentChoice> AskAsync(SimulationRun run, RunInputSnapshot captured, AgentObservation observation, IReadOnlyList<AgentOption> offered,
        IReadOnlyDictionary<Guid, string> names, IReadOnlyDictionary<Guid, string> flights, int? deskCount, ScenarioAgent agent, CancellationToken token)
    {
        var inputs = captured.Scenario.Inputs;
        var day = clock.GetUtcNow().UtcDateTime.Date;
        var used = await db.AiAgentEvents.CountAsync(e => e.Kind == "agent_model_call" && e.RecordedAtUtc >= day, token);
        var scope = new AiAgentEvent { Kind = "agent_model_call", ScenarioId = captured.Scenario.Scenario.Id, RunId = run.Id,
            PromptVersion = AgentPrompts.Version, PromptHash = Hash(AgentPrompts.System(agent.TransferMinutes)) };
        if (used >= options.Value.DailyCallLimit)
        {
            await governance.RecordAsync(Copy(scope, "budget_exhausted", null), token);
            return new("budget_exhausted", null, null, model.Status.Model);
        }
        var state = new
        {
            boarding = observation.Boarding?.Select(f => new
            {
                flight = flights.GetValueOrDefault(f.FlightId, "zbor"), queued = f.Queued, boarding = f.Boarding, boarded = f.Boarded,
                notArrivedYet = f.NotArrived, lanesOpen = 1 + f.OpenGateIds.Count, boardingClosesInMinutes = f.BoardingCloseMinute - observation.Minute,
                minutesToClearQueue = Math.Round(AgentCandidates.Wait(f), 1), inactiveGatesAvailable = f.FreeGateIds.Count
            }),
            controls = observation.Checkpoints.Select(c => new
            {
                name = names.GetValueOrDefault(c.CheckpointId, "control"), operatorsOnDuty = c.StaffOnDuty, closed = !c.Available,
                queued = c.Queued, arrivingNext30Minutes = c.ArrivingNext30Minutes, recentMeanWaitMinutes = c.RecentMeanWaitMinutes,
                longestWaitMinutes = c.LongestWaitMinutes, passengersAtRiskOfMissingBoarding = c.BoardingAtRisk,
                passengersPerMinutePerOperator = c.ServiceRatePerMinute
            }),
            checkIn = observation.CheckIn is { } desks ? new { desksOpen = desks.DesksOpen, desksInArea = deskCount, queued = desks.Queued,
                arrivingNext30Minutes = desks.ArrivingNext30Minutes, recentMeanWaitMinutes = desks.RecentMeanWaitMinutes } : null,
            delay = observation.Gate is { } gate ? new { flight = flights.GetValueOrDefault(gate.FlightId, "zbor"), delayMinutes = gate.DelayMinutes } : null
        };
        var prompt = new AgentPrompt(captured.Scenario.Scenario.Id, run.Id, observation.Minute, Clock(inputs, observation.Minute), state, offered, agent.TransferMinutes);
        var choice = await model.ChooseAsync(prompt, token);
        await governance.RecordAsync(Copy(scope, choice.Status, choice), token);
        return choice;
    }

    private static AiAgentEvent Copy(AiAgentEvent scope, string status, AgentChoice? choice) => new()
    {
        Kind = scope.Kind, Status = status, ScenarioId = scope.ScenarioId, RunId = scope.RunId, PromptVersion = scope.PromptVersion,
        PromptHash = scope.PromptHash, InputTokens = choice?.InputTokens, OutputTokens = choice?.OutputTokens
    };

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string Clock(ScenarioInputs inputs, int minute)
    {
        var instant = inputs.StartsAtUtc.AddMinutes(minute);
        try { instant = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(instant, DateTimeKind.Utc), TimeZoneInfo.FindSystemTimeZoneById(inputs.TimeZone)); }
        catch (Exception failure) when (failure is TimeZoneNotFoundException or InvalidTimeZoneException) { }
        return instant.ToString("HH:mm");
    }
}
