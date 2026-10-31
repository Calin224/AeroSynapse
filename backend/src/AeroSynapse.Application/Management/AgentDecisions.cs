using System.Globalization;
using System.Text.Json.Serialization;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Domain.DigitalTwin;

namespace AeroSynapse.Application.Management;

// What /simulation/v1/observe returns: the run paused after every event of a minute.
public sealed record AgentCheckpointState(Guid CheckpointId, int StaffOnDuty, int PlannedStaff, bool Available, double ServiceRatePerMinute,
    int Queued, int Processing, int Processed, int ArrivingNext30Minutes, double? RecentMeanWaitMinutes, double LongestWaitMinutes,
    int BoardingAtRisk, int? NotArrived);
public sealed record AgentCheckInState(Guid ZoneId, int DesksOpen, double ServiceRatePerMinute, int Queued, int ArrivingNext30Minutes,
    double? RecentMeanWaitMinutes);
public sealed record AgentGateMove(Guid FlightId, Guid FromGateId, Guid ToGateId);
public sealed record AgentGatePlan(IReadOnlyList<AgentGateMove> Moves, double TotalDelayMinutes);
public sealed record AgentGateOptions(Guid EventId, int Minute, Guid FlightId, int DelayMinutes, AgentGatePlan Optimal, AgentGatePlan? KeepGates);
public sealed record AgentObservation(string ObservationVersion, int Minute, IReadOnlyList<AgentCheckpointState> Checkpoints,
    AgentCheckInState? CheckIn, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AgentGateOptions? Gate = null);

/// <summary>One action the algorithm offers; the model picks one by Id.</summary>
public sealed record AgentOption(string Id, string Kind, string Text,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? FromCheckpointId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? ToCheckpointId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Count = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? EventId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? GateMode = null);
public sealed record AgentPrompt(Guid ScenarioId, Guid RunId, int Minute, string Clock, object State, IReadOnlyList<AgentOption> Options, int TransferMinutes);
public sealed record AgentChoice(string Status, string? OptionId, string? Reason, string Model, long? InputTokens = null, long? OutputTokens = null);
public sealed record AgentDecisionView(int Sequence, int Minute, string Point, string Kind, string OptionId, string Text, string Reason,
    string ModelStatus, string Model, IReadOnlyList<AgentOption> Options, DateTime RecordedAtUtc);
public sealed record AgentStatus(bool ModelConfigured, string Model);

public interface IAgentEngine
{
    Task<AgentObservation> ObserveAsync(SimulationRequest request, int minute, Guid? gateEventId, CancellationToken token);
}

public interface IAgentModel
{
    AgentStatus Status { get; }
    Task<AgentChoice> ChooseAsync(AgentPrompt prompt, CancellationToken token);
}

/// <summary>The deterministic options offered to the model at a decision point; "none" is always among them.</summary>
public static class AgentCandidates
{
    public const string None = "none";

    /// <summary>
    /// Moves toward the control with the heaviest load (queue plus the next 30 minutes' arrivals per serving capacity),
    /// from the two lightest controls that keep at least one person, or calls from the reserve still left to it;
    /// desk openings while desks remain.
    /// Controls with scheduled staffing events are left alone, so the agent's steps never conflict with them.
    /// </summary>
    public static IReadOnlyList<AgentOption> Periodic(AgentObservation observation, ScenarioAgent agent, IReadOnlySet<Guid> frozen,
        IReadOnlyDictionary<Guid, string> names, int? deskCount, int reserveLeft = 0)
    {
        var options = new List<AgentOption> { new(None, "none", "Nicio schimbare acum") };
        if (agent.StaffMoves || reserveLeft > 0)
        {
            var open = observation.Checkpoints.Where(c => c.Available && !frozen.Contains(c.CheckpointId)).ToArray();
            static double Load(AgentCheckpointState c, int staff) =>
                (c.Queued + c.ArrivingNext30Minutes) / Math.Max(1e-9, Math.Max(staff, 0) * c.ServiceRatePerMinute * 30);
            var target = open.Where(c => c.Queued + c.ArrivingNext30Minutes > 0)
                .OrderByDescending(c => Load(c, Math.Max(c.PlannedStaff, 1))).ThenBy(c => c.CheckpointId).FirstOrDefault();
            if (target is not null && agent.StaffMoves)
            {
                var donors = open.Where(c => c.CheckpointId != target.CheckpointId && c.PlannedStaff >= 2)
                    .OrderBy(c => Load(c, c.PlannedStaff - 1)).ThenBy(c => c.CheckpointId).Take(2).ToArray();
                foreach (var (donor, index) in donors.Select((donor, index) => (donor, index)))
                {
                    for (var count = 1; count <= (index == 0 && donor.PlannedStaff >= 3 ? 2 : 1); count++)
                        options.Add(new($"move-{count}-{index + 1}", "staff_move",
                            $"Mută {Operators(count)} de la {names.GetValueOrDefault(donor.CheckpointId, "control")} la {names.GetValueOrDefault(target.CheckpointId, "control")}",
                            donor.CheckpointId, target.CheckpointId, count));
                }
            }
            // A small call, a larger one, or the whole remaining reserve.
            if (target is not null)
                foreach (var count in new[] { 2, 5, reserveLeft }.Select(count => Math.Min(count, reserveLeft)).Where(count => count > 0).Distinct().Order())
                    options.Add(new($"call-{count}", "staff_call",
                        $"Cheamă {Operators(count)} din rezervă la {names.GetValueOrDefault(target.CheckpointId, "control")}",
                        ToCheckpointId: target.CheckpointId, Count: count));
        }
        if (agent.CheckInDesks && observation.CheckIn is { } desks && deskCount is { } max)
            for (var count = 1; count <= Math.Min(2, max - desks.DesksOpen); count++)
                options.Add(new($"desk-{count}", "desk_open", count == 1 ? "Deschide un ghișeu de check-in" : $"Deschide {count} ghișee de check-in", Count: count));
        return options;
    }

    /// <summary>Both ways to absorb a scheduled delay; the optimum alone when no plan keeps every gate.</summary>
    public static IReadOnlyList<AgentOption> Gate(AgentGateOptions gate)
    {
        var options = new List<AgentOption> { new("gates-optimal", "gate_choice",
            $"Replanificare optimă: {Moves(gate.Optimal.Moves.Count)}, întârziere totală {Minutes(gate.Optimal.TotalDelayMinutes)}", EventId: gate.EventId, GateMode: "optimal") };
        if (gate.KeepGates is { } kept)
            options.Add(new("gates-keep", "gate_choice", $"Păstrează porțile: întârziere totală {Minutes(kept.TotalDelayMinutes)}", EventId: gate.EventId, GateMode: "keep_gates"));
        return options;
    }

    /// <summary>
    /// The pick when the model gives no answer: the optimum for a delay; otherwise act only where the queue plus the next
    /// 30 minutes' arrivals exceed what serves there, at the most overloaded place, with an option that relieves it.
    /// </summary>
    public static (AgentOption Option, string Reason) Fallback(AgentObservation observation, IReadOnlyList<AgentOption> offered,
        IReadOnlyDictionary<Guid, string> names)
    {
        if (offered[0].Kind == "gate_choice") return (offered[0], "Regulă de rezervă: replanificarea optimă a porților.");
        static double Load(int demand, double servers, double rate) => demand / Math.Max(1e-9, servers * rate * 30);
        var controls = observation.Checkpoints.ToDictionary(c => c.CheckpointId);
        var best = (Option: offered[0], Before: 1.0, Reason: "");
        foreach (var option in offered)
        {
            if (option is { Kind: "staff_move", FromCheckpointId: { } from, ToCheckpointId: { } to, Count: { } count }
                && controls.TryGetValue(from, out var donor) && controls.TryGetValue(to, out var target))
            {
                var demand = target.Queued + target.ArrivingNext30Minutes;
                var before = Load(demand, target.PlannedStaff, target.ServiceRatePerMinute);
                var after = Math.Max(Load(demand, target.PlannedStaff + count, target.ServiceRatePerMinute),
                    Load(donor.Queued + donor.ArrivingNext30Minutes, donor.PlannedStaff - count, donor.ServiceRatePerMinute));
                if (before > best.Before && after < before)
                    best = (option, before, $"Regulă de rezervă: la {names.GetValueOrDefault(to, "control")} așteaptă {target.Queued} și sosesc {target.ArrivingNext30Minutes} în 30 min, peste ce servesc {Operators(target.PlannedStaff)}.");
            }
            else if (option is { Kind: "staff_call", ToCheckpointId: { } called, Count: { } extra } && controls.TryGetValue(called, out var lacking))
            {
                var demand = lacking.Queued + lacking.ArrivingNext30Minutes;
                var before = Load(demand, lacking.PlannedStaff, lacking.ServiceRatePerMinute);
                // A free move wins over the reserve; calls come smallest first, so take more only while fewer still fall short.
                var more = before == best.Before && best.Option is { Kind: "staff_call", Count: { } taken }
                    && Load(demand, lacking.PlannedStaff + taken, lacking.ServiceRatePerMinute) > 1;
                if (before > best.Before || more)
                    best = (option, before, $"Regulă de rezervă: la {names.GetValueOrDefault(called, "control")} așteaptă {lacking.Queued} și sosesc {lacking.ArrivingNext30Minutes} în 30 min, peste ce servesc {Operators(lacking.PlannedStaff)}.");
            }
            else if (option is { Kind: "desk_open", Count: { } desks } && observation.CheckIn is { } checkIn)
            {
                var demand = checkIn.Queued + checkIn.ArrivingNext30Minutes;
                var before = Load(demand, checkIn.DesksOpen, checkIn.ServiceRatePerMinute);
                // Options come one desk at a time: take more desks only while fewer still leave the area overloaded.
                var more = before == best.Before && best.Option is { Kind: "desk_open", Count: { } taken }
                    && Load(demand, checkIn.DesksOpen + taken, checkIn.ServiceRatePerMinute) > 1;
                if (before > best.Before || more)
                    best = (option, before, $"Regulă de rezervă: la check-in așteaptă {checkIn.Queued} și sosesc {checkIn.ArrivingNext30Minutes} în 30 min, peste ce servesc {checkIn.DesksOpen} ghișee.");
            }
        }
        return (best.Option, best.Reason);
    }

    private static string Operators(int count) => count == 1 ? "un operator" : $"{count} operatori";
    private static string Moves(int count) => count == 1 ? "o mutare de poartă" : $"{count} mutări de poartă";
    private static string Minutes(double value) => $"{value.ToString("0.#", CultureInfo.InvariantCulture)} min";
}

/// <summary>Turns decisions into this run's inputs. Each acts after its minute, so earlier history never changes.</summary>
public static class AgentDecisionInputs
{
    public static SimulationRequest Apply(SimulationRequest request, IEnumerable<(int Minute, AgentOption Option)> decisions, int transferMinutes)
    {
        var chosen = decisions.OrderBy(item => item.Minute).ToArray();
        var horizon = request.DurationMinutes;
        var moves = chosen.Where(item => item.Option.Kind is "staff_move" or "staff_call").ToArray();
        if (moves.Length != 0)
        {
            if (request.StaffSchedules is not null) throw new InvalidOperationException("The agent cannot change operators on a shift plan.");
            var counts = request.Checkpoints.ToDictionary(c => c.CheckpointId, c => Enumerable.Repeat(c.StaffCount, horizon).ToArray());
            foreach (var (minute, option) in moves)
            {
                // Operators leave at the next minute and serve again after the transfer; reserve operators only arrive.
                if (option.FromCheckpointId is { } from)
                    for (var at = minute + 1; at < horizon; at++) counts[from][at] -= option.Count!.Value;
                for (var at = minute + 1 + transferMinutes; at < horizon; at++) counts[option.ToCheckpointId!.Value][at] += option.Count!.Value;
            }
            if (counts.Values.Any(row => row.Any(count => count < 0))) throw new InvalidOperationException("A move left a control with negative staff.");
            var changed = counts.Where(pair => pair.Value.Distinct().Count() > 1).OrderBy(pair => pair.Key).ToArray();
            // A scheduled control's staffCount is the most people on duty at once, also in the passenger generator's copy.
            CheckpointInput Nominal(CheckpointInput c) => counts[c.CheckpointId].Distinct().Count() > 1 ? c with { StaffCount = counts[c.CheckpointId].Max() } : c;
            request = request with
            {
                Checkpoints = request.Checkpoints.Select(Nominal).ToArray(),
                PassengerGeneration = request.PassengerGeneration is { } generation ? generation with { Checkpoints = generation.Checkpoints.Select(Nominal).ToArray() } : null,
                StaffSchedules = changed.Select(pair => new StaffSchedule(pair.Key, Steps(pair.Value))).ToArray(),
                Events = request.Events ?? []
            };
        }
        var openings = chosen.Where(item => item.Option.Kind == "desk_open").ToArray();
        if (openings.Length != 0 && request.CheckIn is { } checkIn)
        {
            var steps = new List<StaffStep> { new(0, checkIn.StaffCount) };
            foreach (var group in openings.GroupBy(item => item.Minute + 1).OrderBy(group => group.Key))
                steps.Add(new(group.Key, steps[^1].StaffCount + group.Sum(item => item.Option.Count!.Value)));
            request = request with { CheckIn = checkIn with { StaffCount = steps[^1].StaffCount, DeskSteps = steps } };
        }
        var gates = chosen.Where(item => item.Option.Kind == "gate_choice").ToArray();
        if (gates.Length != 0 && request.Journey is { } journey)
            request = request with { Journey = journey with { GateChoices = gates.Select(item => new JourneyGateChoice(item.Option.EventId!.Value, item.Option.GateMode!)).ToArray() } };
        return request;
    }

    private static IReadOnlyList<StaffStep> Steps(int[] minutes)
    {
        var steps = new List<StaffStep> { new(0, minutes[0]) };
        for (var minute = 1; minute < minutes.Length; minute++)
            if (minutes[minute] != minutes[minute - 1]) steps.Add(new(minute, minutes[minute]));
        return steps;
    }
}

/// <summary>The instructions the model follows; the reason it writes is shown to operators as its own words.</summary>
public static class AgentPrompts
{
    public const string Version = "live-agent/1.1";
    public static string System(int transferMinutes) => $"""
        You are the operations agent of a synthetic airport digital twin (demo data, not a real airport).
        At each decision point you receive the state of the security controls, the check-in desks or a flight delay,
        and options computed by a deterministic algorithm. Choose exactly one option id.
        Prefer options that reduce passenger waiting and the number of passengers at risk of missing boarding.
        Do not move people when queues are short: an operator who moves serves nobody for {transferMinutes} minutes.
        Calling reserve operators costs staff and they also serve only after {transferMinutes} minutes: prefer moving
        idle operators, and call the reserve only when a queue and its arrivals clearly outgrow the operators on duty.
        Opening a desk costs staff, so open one only when the check-in queue or its arrivals justify it.
        For a delay, moving flights to other gates shortens delays but sends passengers walking to new gates.
        Reply with JSON: "optionId" (one of the given ids) and "reason": one short sentence in Romanian, at most
        200 characters, citing only numbers from the input. Never invent numbers, names or actions.
        """;
}
