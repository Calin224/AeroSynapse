using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
/// <summary>A flight whose boarding is open: its queue, the gates already serving it and the gates nobody is planned at.</summary>
public sealed record AgentBoardingState(Guid FlightId, Guid GateId, IReadOnlyList<Guid> OpenGateIds, IReadOnlyList<Guid> FreeGateIds,
    double BoardingCloseMinute, int Queued, int Boarding, int Boarded, int NotArrived, int BoardingStaffPerGate, double BoardingServiceMinutes);
public sealed record AgentPlanTarget(Guid CheckpointId, int Current, int Target);
/// <summary>The optimized plan for the next hour: desk levels from step minutes on, and the operators each control should have.</summary>
public sealed record AgentRecommendation(int BlockMinutes, int Blocks, IReadOnlyList<StaffStep>? Desks, IReadOnlyList<AgentPlanTarget>? Staff, int ReserveUsed);
/// <summary>What the planner may use: the desks of the area, the reserve left and the transfer time; frozen controls are left out.</summary>
public sealed record AgentLimits(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? DeskLimit, int Reserve, int TransferMinutes, IReadOnlyList<Guid> Frozen);
public sealed record AgentObservation(string ObservationVersion, int Minute, IReadOnlyList<AgentCheckpointState> Checkpoints,
    AgentCheckInState? CheckIn, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AgentGateOptions? Gate = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<AgentBoardingState>? Boarding = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AgentRecommendation? Recommendation = null);

/// <summary>A staff member who can open a gate: not reserved for security or another team in the scenario.</summary>
public sealed record AgentGateStaff(Guid Id, Guid TerminalId, string Name);

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
public sealed record AgentDecisionView(int Sequence, int Minute, string Point, string Kind, string OptionId, string Text, string Reason,
    string ModelStatus, string Model, IReadOnlyList<AgentOption> Options, DateTime RecordedAtUtc);
public sealed record AgentStatus(bool ModelConfigured, string Model);

public interface IAgentEngine
{
    Task<AgentObservation> ObserveAsync(SimulationRequest request, int minute, Guid? gateEventId, AgentLimits? limits, CancellationToken token);
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
        if (observation.Recommendation is { } plan)
        {
            var staff = agent.StaffMoves && plan.Staff is { } targets && plan.ReserveUsed <= reserveLeft
                ? targets.Where(t => t.Target != t.Current && t.Target >= 1 && !frozen.Contains(t.CheckpointId)).ToArray() : [];
            var deskPlan = agent.CheckInDesks && deskCount is { } area && plan.Desks is { Count: > 0 } steps && steps.All(s => s.StaffCount <= area) ? steps : null;
            var staffText = string.Join(", ", staff.Select(t =>
                $"{names.GetValueOrDefault(t.CheckpointId, "control")} {(t.Target - t.Current).ToString("+0;-0", CultureInfo.InvariantCulture)}"));
            var deskText = deskPlan is null ? "" : string.Join(", ", deskPlan.Select(s => $"{s.StaffCount} ghișee de la min {s.StartMinute}"));
            var used = staff.Length == 0 ? 0 : plan.ReserveUsed;
            if (staff.Length > 0 && deskPlan is not null)
            {
                options.Add(new("plan", "plan", $"Aplică planul optim: operatori {staffText}; check-in {deskText}", Count: used, Staff: staff, Desks: deskPlan));
                options.Add(new("plan-staff", "plan", $"Aplică doar planul de operatori: {staffText}", Count: used, Staff: staff));
                options.Add(new("plan-desks", "plan", $"Aplică doar planul de ghișee: {deskText}", Desks: deskPlan));
            }
            else if (staff.Length > 0)
                options.Add(new("plan", "plan", $"Aplică planul optim de operatori: {staffText}", Count: used, Staff: staff));
            else if (deskPlan is not null)
                options.Add(new("plan", "plan", $"Aplică planul optim de ghișee: {deskText}", Desks: deskPlan));
        }
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
        if (agent.StaffMoves) options.AddRange(QueueActions(observation, agent, frozen, names, reserveLeft));
        return options;
    }

    private static IEnumerable<AgentOption> QueueActions(AgentObservation observation, ScenarioAgent agent, IReadOnlySet<Guid> frozen,
        IReadOnlyDictionary<Guid, string> names, int reserve)
    {
        var available = observation.Checkpoints.Where(c => c.Available && !frozen.Contains(c.CheckpointId)).ToArray();
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
                if (Wait(source) <= agent.TransferMinutes + 2) continue;
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
                (source.Queued / sourceRate - targetWait - Math.Max(1, agent.TransferMinutes)) / (1 / sourceRate + 1 / targetRate))));
            if (count <= 0) continue;
            var from = names.GetValueOrDefault(source.CheckpointId, "control");
            var to = names.GetValueOrDefault(target.CheckpointId, "control");
            yield return new($"{(opening ? "open" : "queue")}-{target.CheckpointId:N}", opening ? "checkpoint_open" : "queue_redistribution",
                opening ? $"Deschide {to} cu un operator și distribuie până la {count} pasageri din coada de la {from}"
                        : $"Distribuie până la {count} pasageri din coada de la {from} către {to}",
                source.CheckpointId, target.CheckpointId, opening ? reserveUsed : null, Staff: opening ? staff : null, PassengerCount: count);
        }
    }

    /// <summary>
    /// Opening an inactive gate for a flight whose boarding queue would take long to clear: the flight keeps its gate and
    /// gains a lane at a gate nobody is planned at, run by a free staff member of that terminal. Never a gate swap.
    /// </summary>
    public static IReadOnlyList<AgentOption> GateOpenings(AgentObservation observation, IReadOnlyList<AgentGateStaff> staff,
        IReadOnlyDictionary<Guid, Guid> gateTerminals, IReadOnlyDictionary<Guid, string> gateNames, IReadOnlyDictionary<Guid, string> flightNames)
    {
        var options = new List<AgentOption>();
        foreach (var flight in (observation.Boarding ?? []).Where(f => f.Queued > 0 && Wait(f) >= 2 * f.BoardingServiceMinutes)
                     .OrderByDescending(Wait).ThenBy(f => f.FlightId).Take(2))
        {
            foreach (var gate in flight.FreeGateIds.Order())
            {
                var member = staff.Where(s => gateTerminals.TryGetValue(gate, out var terminal) && s.TerminalId == terminal)
                    .OrderBy(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.Id).FirstOrDefault();
                if (member is null) continue;
                options.Add(new($"gate-open-{flight.FlightId:N}-{gate:N}", "gate_open",
                    $"{member.Name} deschide poarta {gateNames.GetValueOrDefault(gate, "poartă")} pentru zborul {flightNames.GetValueOrDefault(flight.FlightId, "zbor")}: " +
                    $"{flight.Queued} la coadă, ~{Wait(flight).ToString("0", CultureInfo.InvariantCulture)} min până se golește, zborul își păstrează poarta",
                    EventId: null, FlightId: flight.FlightId, GateId: gate, StaffMemberId: member.Id));
                break;
            }
        }
        return options;
    }

    /// <summary>Minutes the boarding queue needs to clear with the lanes serving the flight now.</summary>
    public static double Wait(AgentBoardingState flight) =>
        flight.Queued * flight.BoardingServiceMinutes / Math.Max(1, flight.BoardingStaffPerGate * (1 + flight.OpenGateIds.Count));

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
        // The optimized plan replaces the one-step rules for staffing and desks; delays and boarding keep theirs.
        var plan = offered.FirstOrDefault(option => option.Kind == "plan");
        var queue = offered.Where(o => o.Kind is "checkpoint_open" or "queue_redistribution")
            .OrderByDescending(o => o.PassengerCount).ThenBy(o => o.Kind == "checkpoint_open" ? 1 : 0).FirstOrDefault();
        var best = (Option: offered[0], Before: 1.0, Reason: "");
        foreach (var option in offered)
        {
            if (plan is not null && option.Kind is "staff_move" or "staff_call" or "desk_open") continue;
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
            else if (option is { Kind: "gate_open", FlightId: { } flightId } && observation.Boarding?.FirstOrDefault(f => f.FlightId == flightId) is { } boarding)
            {
                // The queue cannot clear before boarding closes at the current lanes: wait over the minutes left.
                var before = Wait(boarding) / Math.Max(1, boarding.BoardingCloseMinute - observation.Minute);
                if (before > best.Before)
                    best = (option, before, $"Regulă de rezervă: {boarding.Queued} pasageri la coadă nu urcă până la închiderea îmbarcării cu poarta actuală (~{Wait(boarding).ToString("0", CultureInfo.InvariantCulture)} min necesare).");
            }
        }
        if (plan is not null && best.Option.Kind == "none")
            return (plan, "Regulă de rezervă: planul optim anticipează sosirile următoarei ore și repartizează operatorii și ghișeele pentru a evita aglomerarea.");
        if (queue is not null && best.Option.Kind == "none") return (queue, "Regulă de rezervă: distribuie coada către un control disponibil cu capacitate liberă, cu personal și timp de deplasare.");
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
        var moves = chosen.Where(item => item.Option.Kind is "staff_move" or "staff_call" or "checkpoint_open" || item.Option is { Kind: "plan", Staff: not null }).ToArray();
        if (moves.Length != 0)
        {
            if (request.StaffSchedules is not null) throw new InvalidOperationException("The agent cannot change operators on a shift plan.");
            var counts = request.Checkpoints.ToDictionary(c => c.CheckpointId, c => Enumerable.Repeat(c.StaffCount, horizon).ToArray());
            foreach (var (minute, option) in moves)
            {
                if (option.Kind is "plan" or "checkpoint_open")
                {
                    // Operators taken away stop at the next minute; those added serve after the transfer.
                    foreach (var target in option.Staff!.Where(t => counts.ContainsKey(t.CheckpointId)))
                        for (var at = minute + 1 + (target.Target > target.Current ? transferMinutes : 0); at < horizon; at++)
                            counts[target.CheckpointId][at] += target.Target - target.Current;
                    continue;
                }
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
        var transfers = chosen.Where(item => item.Option.Kind is "checkpoint_open" or "queue_redistribution").ToArray();
        if (transfers.Length > 0)
        {
            var events = (request.Events ?? []).ToList();
            foreach (var (minute, option) in transfers)
            {
                var at = minute + 1 + transferMinutes;
                var walking = Math.Max(1, transferMinutes);
                if (at + walking >= horizon) continue;
                var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"queue/{request.RunId:N}/{minute}/{option.Id}")).AsSpan(0, 16));
                events.Add(new(id, "queue_redistribution", at, CheckpointId: option.FromCheckpointId,
                    TargetCheckpointId: option.ToCheckpointId, PassengerCount: option.PassengerCount, WindowMinutes: walking));
            }
            request = request with { Events = events };
        }
        var deskDecisions = chosen.Where(item => item.Option.Kind == "desk_open" || item.Option is { Kind: "plan", Desks: not null }).ToArray();
        if (deskDecisions.Length != 0 && request.CheckIn is { } checkIn)
        {
            // One level per minute: openings add desks from the next minute, a plan sets the levels of its steps.
            var levels = Enumerable.Range(0, horizon).Select(at => checkIn.OpenAt(at)).ToArray();
            foreach (var (minute, option) in deskDecisions)
            {
                if (option.Kind == "desk_open")
                    for (var at = minute + 1; at < horizon; at++) levels[at] += option.Count!.Value;
                else
                    foreach (var (step, index) in option.Desks!.Select((step, index) => (step, index)))
                        for (var at = Math.Max(step.StartMinute, minute + 1); at < (index + 1 < option.Desks!.Count ? option.Desks[index + 1].StartMinute : horizon) && at < horizon; at++)
                            levels[at] = step.StaffCount;
            }
            request = request with { CheckIn = checkIn with { StaffCount = levels.Max(), DeskSteps = Steps(levels) } };
        }
        var gates = chosen.Where(item => item.Option.Kind == "gate_choice").ToArray();
        if (gates.Length != 0 && request.Journey is { } journey)
            request = request with { Journey = journey with { GateChoices = gates.Select(item => new JourneyGateChoice(item.Option.EventId!.Value, item.Option.GateMode!)).ToArray() } };
        var opened = chosen.Where(item => item.Option.Kind == "gate_open").ToArray();
        if (opened.Length != 0 && request.Journey is { } flightsJourney)
            request = request with
            {
                Journey = flightsJourney with
                {
                    GateOpenings = opened.Select(item => new JourneyGateOpening(item.Option.FlightId!.Value, item.Option.GateId!.Value,
                        item.Option.StaffMemberId!.Value, item.Minute + 1)).ToArray()
                }
            };
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
