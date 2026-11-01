using System.Text.Json;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Management;
using AeroSynapse.Application.Storage;
using AeroSynapse.Infrastructure.Storage;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AeroSynapse.Infrastructure.Governance;

namespace AeroSynapse.Infrastructure.Execution;

public sealed class CopilotTools(TwinDbContext db, ICopilotService copilot, IReplayService replay, ISender sender,
    IScenarioService scenarios, IBatchService batches, IRecommendationReviewService reviews, ILogger<CopilotTools> logger, AiGovernance governance) : ICopilotTools
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task<CopilotToolResult> ExecuteAsync(Guid scenarioId, Guid runId, CopilotToolRequest request, CancellationToken token)
    {
        var policyVersion = await governance.AuthorizeToolAsync(scenarioId, runId, request.Call?.Name ?? "unknown", token);
        try
        {
            var result = await ExecuteCore(scenarioId, runId, request, token);
            await Record("completed"); return result;
        }
        catch (Exception error)
        {
            await Record(error is CopilotToolRejectedException rejected ? rejected.Code : error is OperationCanceledException ? "cancelled" : "tool_failed");
            throw;
        }
        async Task Record(string status)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await governance.RecordAsync(new() { Kind = "tool_result", Status = status, Tool = request.Call?.Name,
                ScenarioId = scenarioId, RunId = runId, PolicyVersion = policyVersion }, deadline.Token);
        }
    }
    private async Task<CopilotToolResult> ExecuteCore(Guid scenarioId, Guid runId, CopilotToolRequest request, CancellationToken token)
    {
        var tool = CopilotToolPolicy.Admit(request);
        // Validate every argument before loading data or calling an engine; no free-form dispatcher/reflection.
        object arguments = tool.Name switch
        {
            "inspect_zone" => CopilotToolPolicy.Parse<CopilotInspectionArguments>(request.Call.Arguments),
            "forecast_queues" => CopilotToolPolicy.Parse<CopilotForecastArguments>(request.Call.Arguments),
            "staffing_settings" => CopilotToolPolicy.Parse<CopilotNoArguments>(request.Call.Arguments),
            "evaluate_staffing" => CopilotToolPolicy.Parse<CopilotEvaluationArguments>(request.Call.Arguments),
            _ => CopilotToolPolicy.Parse<CopilotEvaluationReference>(request.Call.Arguments)
        };
        var context = await copilot.ContextAsync(scenarioId, runId, token);
        if (!context.Zones.Any(z => z.Id == request.ZoneId) || request.Minute < 0 || request.Minute > context.DurationMinutes)
            throw Reject("invalid_tool_context", "zoneId/minute", "Alege o zonă și un minut din snapshot-ul rulării.");
        var scope = new CopilotScope(scenarioId, runId, request.ZoneId, request.Minute);
        var output = new CopilotToolResult("1.0", scope, request.Intent, tool.Name, tool.Effect, tool.Description);
        try
        {
            if (arguments is CopilotInspectionArguments inspect)
            {
                var observation = await copilot.AskAsync(scenarioId, runId, new(request.ZoneId, inspect.Question, request.Minute), token);
                IReadOnlyList<CopilotEventFact> events = [];
                if (context.ResultsValid && context.UnavailableReason is null)
                {
                    var data = await Capture(runId, token);
                    // Never expose or interpret PayloadJson, scenario descriptions, or event free text as instructions.
                    events = data.Scenario.Inputs.Events.Where(e => CopilotRunAnswers.EventType(e.Type) is not null && (e.CheckpointId is null || e.CheckpointId == request.ZoneId))
                        .Select(e => new CopilotEventFact(e.Id, CopilotRunAnswers.EventType(e.Type)!, (e.OccurredAtUtc - data.Scenario.Inputs.StartsAtUtc).TotalMinutes, e.CheckpointId))
                        .Where(e => e.Minute >= 0 && e.Minute <= request.Minute).OrderBy(e => e.Minute).ThenBy(e => e.Id).Take(100).ToArray();
                }
                return Done(output with { Investigation = new(observation, events) });
            }
            if (!context.ResultsValid || context.UnavailableReason is not null)
                throw Reject("evidence_unavailable", "runId", "Unealta necesită o rulare finalizată cu intrări capturate valide.");
            if (arguments is CopilotForecastArguments forecast)
            {
                if (forecast.HorizonMinutes is < 15 or > 30 || forecast.QueueThresholdPassengers is < 0 or > 100000
                    || request.Minute + forecast.HorizonMinutes > context.DurationMinutes)
                    throw Reject("invalid_tool_arguments", "horizonMinutes", "Prognoza trebuie să aibă 15–30 minute în interiorul rulării și prag între 0 și 100000 pasageri.");
                var value = await sender.Send(new ForecastQueuesQuery(runId, new(request.Minute, forecast.HorizonMinutes, forecast.QueueThresholdPassengers)), token);
                var zone = value.Checkpoints.SingleOrDefault(z => z.CheckpointId == request.ZoneId)
                    ?? throw Reject("invalid_tool_result", "zoneId", "Prognoza nu conține zona selectată.");
                return Done(output with { Forecast = new(value.Kind, value.Formula, runId, value.FromMinute, value.ToMinute, value.QueueUnit, value.FlowUnit, value.TimeUnit, zone) });
            }
            var captured = await Capture(runId, token);
            if (arguments is CopilotNoArguments)
            {
                await Current(captured, token);
                return Done(output with { Staffing = await sender.Send(new GetStaffingSettingsQuery(scenarioId), token) });
            }
            if (arguments is CopilotEvaluationArguments evaluate)
            {
                if (!context.IsSynthetic || captured.Engine != KpiConventions.Engine)
                    throw Reject("optimization_not_supported", "runId", "Optimizarea este disponibilă numai pentru rulări sintetice cu motor KPI.");
                if (evaluate.CommandId == Guid.Empty || evaluate.CandidateCount is < 1 or > 3 || evaluate.TimeLimitSeconds is < 15 or > 120
                    || evaluate.Proposal is null || evaluate.Proposal.TimeLimitSeconds is < 0.1 or > 5
                    || evaluate.Proposal.Revision != context.ScenarioRevision)
                    throw Reject("invalid_tool_arguments", "proposal", "Sunt necesare commandId unic, revizia capturată, 1–3 candidați, buget de 15–120 s și solver de 0.1–5 s.");
                // Reuse the same idempotent command even after a response was lost. It must already match this baseline.
                if (!await db.Batches.AsNoTracking().AnyAsync(b => b.Id == evaluate.CommandId, token)) await Current(captured, token);
                var value = await batches.CreateStaffingEvaluationAsync(scenarioId, new(evaluate.CommandId, "Copilot · optimizare DEMO",
                    evaluate.Proposal, evaluate.CandidateCount, new(), evaluate.TimeLimitSeconds), token);
                await SameBaseline(value, captured, token);
                return Done(output with { Evaluation = value });
            }
            var reference = (CopilotEvaluationReference)arguments;
            if (reference.EvaluationId == Guid.Empty) throw Reject("invalid_tool_arguments", "evaluationId", "Introdu un evaluationId nenul.");
            var evaluation = await batches.GetStaffingEvaluationAsync(reference.EvaluationId, token);
            await SameBaseline(evaluation, captured, token);
            if (tool.Name == "prepare_recommendation")
                return Done(output with { Evaluation = evaluation, Recommendation = await reviews.CreateAsync(reference.EvaluationId, token) });
            if (tool.Name == "cancel_evaluation")
            {
                await batches.CancelAsync(reference.EvaluationId, token);
                evaluation = await batches.GetStaffingEvaluationAsync(reference.EvaluationId, token);
            }
            return Done(output with { Evaluation = evaluation });
        }
        catch (ScenarioValidationException error)
        { throw Reject("invalid_tool_arguments", string.Join(", ", error.Errors.Keys), string.Join(" ", error.Errors.Values.SelectMany(e => e))); }
        catch (ContractValidationException) { throw Reject("invalid_tool_arguments", "call.arguments", "Argumentele nu respectă contractul versionat al solverului sau prognozei."); }
        catch (Exception error) when (error is QueueForecasterUnavailableException or StaffingPlannerUnavailableException or PassengerGeneratorUnavailableException)
        { throw new CopilotToolRejectedException("tool_unavailable", "call", "Serviciul de calcul nu este disponibil sau a returnat date invalide. Nu există rezultate calculate valide pentru acest apel.", 502); }
    }
    private CopilotToolResult Done(CopilotToolResult output)
    {
        logger.LogInformation("Copilot tool={Tool} intent={Intent} effect={Effect} scenarioId={ScenarioId} runId={RunId} zoneId={ZoneId}",
            output.Tool, output.Intent, output.Effect, output.Scope.ScenarioId, output.Scope.RunId, output.Scope.ZoneId);
        return output;
    }
    private async Task<RunInputSnapshot> Capture(Guid id, CancellationToken token)
    {
        var envelope = await replay.GetAsync(id, token);
        return JsonSerializer.Deserialize<ReplayData>(envelope.PayloadJson, Json)!.Captured;
    }
    private async Task Current(RunInputSnapshot captured, CancellationToken token)
    {
        var current = await scenarios.GetAsync(captured.Scenario.Scenario.Id, token);
        if (current is null || current.Scenario.DeletedAtUtc is not null || current.Scenario.Revision != captured.Scenario.Scenario.Revision
            || current.Scenario.LayoutVersionId != captured.Scenario.Scenario.LayoutVersionId
            || !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(FlightScheduling.Resolve(current.Inputs), Json), JsonSerializer.SerializeToElement(captured.Scenario.Inputs, Json)))
            throw new CopilotToolRejectedException("stale_scenario", "scenarioId", "Scenariul a fost editat sau arhivat după captură. Lansează o rulare a reviziei curente înainte de o nouă optimizare.", 409);
    }
    private async Task SameBaseline(StaffingEvaluationDetails value, RunInputSnapshot captured, CancellationToken token)
    {
        var baseline = value.Variants.Single(v => v.Position == 0);
        var run = await db.Runs.AsNoTracking().SingleAsync(r => r.Id == baseline.RunId, token);
        var input = JsonSerializer.Deserialize<RunInputSnapshot>(run.InputSnapshotJson!, Json)!;
        if (baseline.ScenarioId != captured.Scenario.Scenario.Id || input.Scenario.Scenario.Revision != captured.Scenario.Scenario.Revision
            || input.Layout.Version.Id != captured.Layout.Version.Id
            || !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(input.Scenario.Inputs, Json), JsonSerializer.SerializeToElement(captured.Scenario.Inputs, Json)))
            throw new CopilotToolRejectedException("evaluation_context_mismatch", "evaluationId", "Evaluarea nu aparține intrărilor capturate ale rulării selectate.", 409);
    }
    private static CopilotToolRejectedException Reject(string code, string field, string message) => new(code, field, message);
}
