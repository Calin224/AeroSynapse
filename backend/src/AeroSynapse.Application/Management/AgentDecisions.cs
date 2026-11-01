using System.Globalization;
using System.Text.Json.Serialization;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Domain.DigitalTwin;

namespace AeroSynapse.Application.Management;

public sealed record AgentPlanTarget(Guid CheckpointId, int Current, int Target);
/// <summary>One security control at a minute, as the Copilot's queue advice reads it from the run's KPIs.</summary>
public sealed record AgentCheckpointState(Guid CheckpointId, int StaffOnDuty, int PlannedStaff, bool Available, double ServiceRatePerMinute,
    int Queued, int Processing, int Processed, int ArrivingNext30Minutes, double? RecentMeanWaitMinutes, double LongestWaitMinutes,
    int BoardingAtRisk, int? NotArrived);

/// <summary>One action the algorithm offers; the model picks one by Id.</summary>
public sealed record AgentOption(string Id, string Kind, string Text,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? FromCheckpointId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? ToCheckpointId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Count = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? EventId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? GateMode = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? FlightId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? GateId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? StaffMemberId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<AgentPlanTarget>? Staff = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<StaffStep>? Desks = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? PassengerCount = null);
public sealed record AgentPrompt(Guid ScenarioId, Guid RunId, int Minute, string Clock, object State, IReadOnlyList<AgentOption> Options, int TransferMinutes);
public sealed record AgentChoice(string Status, string? OptionId, string? Reason, string Model, long? InputTokens = null, long? OutputTokens = null);
public sealed record AgentStatus(bool ModelConfigured, string Model);

public interface IAgentModel
{
    AgentStatus Status { get; }
    Task<AgentChoice> ChooseAsync(AgentPrompt prompt, CancellationToken token);
    /// <summary>Rewords code-computed facts of already simulated options; never picks, adds numbers or new options.</summary>
    Task<OptimizationAdvice> AdviseAsync(object facts, IReadOnlyList<string> optionIds, CancellationToken token) =>
        Task.FromResult(new OptimizationAdvice("unconfigured", Status.Model));
}

public static class AgentCandidates
{
    /// <summary>Kind of a stored decision that changed nothing; older runs keep such rows.</summary>
    public const string None = "none";

    /// <summary>
    /// Ways to relieve the control with the longest expected wait without new staffing rules: open an available unstaffed
    /// control (with an idle operator, or one from the reserve) or send waiting passengers to a staffed control with spare
    /// capacity, each with the most passengers worth moving given the walk. Used by the Copilot's queue advice.
    /// </summary>
    public static IEnumerable<AgentOption> QueueActions(IReadOnlyList<AgentCheckpointState> checkpoints, int transferMinutes,
        IReadOnlySet<Guid> frozen, IReadOnlyDictionary<Guid, string> names, int reserve)
    {
        var available = checkpoints.Where(c => c.Available && !frozen.Contains(c.CheckpointId)).ToArray();
        static double Rate(AgentCheckpointState c) => c.PlannedStaff * c.ServiceRatePerMinute;
        static double Wait(AgentCheckpointState c) => c.Queued / Math.Max(1e-9, Rate(c));
        var source = available.Where(c => c.Queued > 0).OrderByDescending(Wait).ThenBy(c => c.CheckpointId).FirstOrDefault();
        if (source is null) yield break;
        foreach (var target in available.Where(c => c.CheckpointId != source.CheckpointId).OrderBy(c => c.CheckpointId))
        {
            var opening = target.PlannedStaff == 0;
            var staff = new List<AgentPlanTarget>();
            var reserveUsed = 0;
            if (opening)
            {
                // Open only for a sustained queue; keep at least one operator at the donor.
                if (Wait(source) <= transferMinutes + 2) continue;
                var donor = available.Where(c => c.CheckpointId != target.CheckpointId && c.CheckpointId != source.CheckpointId && c.PlannedStaff >= 2
                    && (c.Queued + c.ArrivingNext30Minutes) / ((c.PlannedStaff - 1) * c.ServiceRatePerMinute * 30) < 0.8)
                    .OrderBy(Wait).ThenBy(c => c.CheckpointId).FirstOrDefault();
                if (donor is not null) staff.Add(new(donor.CheckpointId, donor.PlannedStaff, donor.PlannedStaff - 1));
                else if (reserve > 0) reserveUsed = 1;
                else continue;
                staff.Add(new(target.CheckpointId, 0, 1));
            }
            var sourceRate = Math.Max(source.ServiceRatePerMinute, Rate(source));
            var targetRate = opening ? target.ServiceRatePerMinute : Rate(target);
            var targetWait = target.Queued / Math.Max(1e-9, targetRate);
            var count = Math.Min(10000, Math.Min(source.Queued, (int)Math.Floor(
                (source.Queued / sourceRate - targetWait - Math.Max(1, transferMinutes)) / (1 / sourceRate + 1 / targetRate))));
            if (count <= 0) continue;
            var from = names.GetValueOrDefault(source.CheckpointId, "control");
            var to = names.GetValueOrDefault(target.CheckpointId, "control");
            yield return new($"{(opening ? "open" : "queue")}-{target.CheckpointId:N}", opening ? "checkpoint_open" : "queue_redistribution",
                opening ? $"Deschide {to} cu un operator și distribuie până la {count} pasageri din coada de la {from}"
                        : $"Distribuie până la {count} pasageri din coada de la {from} către {to}",
                source.CheckpointId, target.CheckpointId, opening ? reserveUsed : null, Staff: opening ? staff : null, PassengerCount: count);
        }
    }
}

/// <summary>The instructions the model follows; the reason it writes is shown to operators as its own words.</summary>
public static class AgentPrompts
{
    public const string Version = "live-agent/1.4";
    public static string System(int transferMinutes) => $"""
        You are the operations agent of a synthetic airport digital twin (demo data, not a real airport).
        At each decision point you receive the state of the security controls, the check-in desks, the boarding queues or a flight delay,
        and options computed by a deterministic algorithm. Choose exactly one option id.
        Prefer options that reduce passenger waiting and the number of passengers at risk of missing boarding.
        Do not move people when queues are short: an operator who moves serves nobody for {transferMinutes} minutes.
        Calling reserve operators costs staff and they also serve only after {transferMinutes} minutes: prefer moving
        idle operators, and call the reserve only when a queue and its arrivals clearly outgrow the operators on duty.
        A checkpoint_open option staffs an available unused security control, then transfers waiting passengers there.
        A queue_redistribution option moves only waiting passengers to a staffed control with spare capacity.
        Their counts are upper bounds; the simulator checks availability and access again when the move occurs.
        Prefer distributing a long queue over adding operators to the same control when a usable alternative exists.
        Passengers keep their flights and must still complete security; transfers take walking time.
        Opening a desk costs staff, so open one only when the check-in queue or its arrivals justify it.
        A "plan" option is the optimum of a mathematical model over the next hour of expected arrivals: it staffs the controls
        and desks before a wave and releases them after it. Prefer it over single-step options when it is offered,
        unless a checkpoint opening or queue redistribution can use idle capacity for the existing queue.
        Take a staffing plan whole unless its text contradicts the numbers you are given.
        For a delay, moving flights to other gates shortens delays but sends passengers walking to new gates.
        A staff member can open a gate where no flight is planned to add a boarding lane for a flight with a long queue;
        the flight keeps its gate. An open gate with nobody boarding is wasted: open one only when the boarding queue
        would not clear before boarding closes, or clearly outlasts what remains of the boarding window.
        Reply with JSON: "optionId" (one of the given ids) and "reason": one short sentence in Romanian, at most
        200 characters, citing only numbers from the input. Never invent numbers, names or actions.
        """;
}
