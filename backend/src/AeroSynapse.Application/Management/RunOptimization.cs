using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.DigitalTwin;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AeroSynapse.Application.Management;

/// <summary>A staffing alternative to a finished run: operators added (+) or removed (−) per security control, and check-in desks opened.</summary>
public sealed record OptimizationCandidate(string Id, string Kind, string Action, IReadOnlyDictionary<Guid, int> StaffDelta, int DeskDelta = 0)
{
    // An open desk is one check-in operator.
    public int OperatorDelta => StaffDelta.Values.Sum() + DeskDelta;
}

/// <summary>
/// One option after its simulation: the numbers only from the simulator, the card's title is the option's own action and
/// the explanation is the model's sentence only when it agrees with the numbers (otherwise the deterministic facts).
/// Only options the scenario's own people can staff are simulated, so every option can be applied.
/// </summary>
public sealed record RunOptimizationOption(string Id, string Kind, string Action, int OperatorDelta, AgentImpactSide Result,
    string Explanation, bool Recommended, IReadOnlyDictionary<Guid, int> StaffDelta, int DeskDelta = 0);

public sealed record OptimizationApplyRequest(IReadOnlyDictionary<Guid, int>? StaffDelta, int DeskDelta = 0);

/// <summary>The scenario's check-in desks at the reference simulation: Open of the area's Desks.</summary>
public sealed record OptimizationDesks(string Name, int Open, int Desks, double? P95WaitMinutes, int MaximumQueue);

/// <summary>Per control at the reference simulation, so the model can name where the problem is.</summary>
public sealed record OptimizationControl(string Name, int Staff, double? P95WaitMinutes, int MaximumQueue);

/// <summary>The optimization proposed after a run: the same day and seed simulated again with each option.</summary>
/// <remarks>With CheckIn the waits include the desks. Notice says, in Romanian, why the bottleneck gets no extra people.</remarks>
public sealed record RunOptimization(string ModelStatus, string Model, string? Summary, AgentImpactSide Baseline,
    IReadOnlyList<OptimizationControl> Controls, IReadOnlyList<RunOptimizationOption> Options,
    OptimizationDesks? CheckIn = null, string? Notice = null, bool Optimal = false);

public sealed record OptimizationAdviceOption(string Id, string Explanation);
/// <summary>The model's words: a sentence per option and Advice, the summary's second sentence about the recommendation.</summary>
public sealed record OptimizationAdvice(string Status, string Model, string? Advice = null, IReadOnlyList<OptimizationAdviceOption>? Options = null);

/// <summary>
/// One option's numbers digested in code, so the model gets words to rephrase, never doubles to compare. Verdict uses the
/// recommendation's 5% thresholds: "better" (wait at least 5% shorter), "worse" (at least 5% longer) or "same".
/// Facts is the deterministic sentence shown when the model's own sentence is missing or contradicts the numbers.
/// </summary>
public sealed record OptimizationFacts(string Id, string Action, bool Recommended, string Verdict, string Wait, string MaxQueue,
    string? P95, string Staff, string Facts);

/// <summary>The longest queue at any moment (the run summary's "busiest moment"): Place, people queued and the minute.</summary>
public sealed record OptimizationPeak(string Place, int Queued, int Minute);

/// <summary>
/// Everything the panel says that code can decide. The summary is Where (the bottleneck, always from code) and Advice
/// (about the recommendation), which the model may rephrase; Recommendation is the recommended action and why, or null.
/// </summary>
public sealed record OptimizationBrief(OptimizationPeak? Bottleneck, string? Recommendation, IReadOnlyList<OptimizationFacts> Options, string Where, string Advice)
{
    public string Summary => $"{Where} {Advice}";
}

public static class RunOptimizationPlanner
{
    /// <summary>
    /// Up to four alternatives around the control with the longest queue ("hot") and the calmest control that keeps
    /// at least one operator ("cold"): move one operator, add one, add two, and save one where it is least needed.
    /// Controls with a shift plan are left out: the plan, not the staff count, decides who is on duty there.
    /// With queued check-in desks, one and two more open desks too, ahead of the controls when the desks are the bottleneck.
    /// Whether the scenario has the people for an option is decided afterwards by <see cref="Staff"/>.
    /// </summary>
    public static IReadOnlyList<OptimizationCandidate> Candidates(IReadOnlyList<CheckpointInput> checkpoints, IReadOnlyList<KpiSnapshot> baseline,
        IReadOnlyDictionary<Guid, string> names, IReadOnlySet<Guid> scheduled, OptimizationDesks? desks = null)
    {
        var queue = baseline.GroupBy(s => s.ZoneId).ToDictionary(g => g.Key, g => g.Max(s => s.QueuedPassengers));
        var p95 = baseline.GroupBy(s => s.ZoneId).ToDictionary(g => g.Key, g => g.MaxBy(s => s.Minute)!.P95WaitMinutes ?? 0);
        var open = checkpoints.Where(c => !scheduled.Contains(c.CheckpointId) && c.StaffCount >= 1)
            .OrderByDescending(c => queue.GetValueOrDefault(c.CheckpointId)).ThenByDescending(c => p95.GetValueOrDefault(c.CheckpointId))
            .ThenBy(c => c.CheckpointId).ToArray();
        string Name(Guid id) => names.GetValueOrDefault(id, "control");
        var candidates = new List<OptimizationCandidate>();
        var hot = open.FirstOrDefault();
        var queued = hot is not null && queue.GetValueOrDefault(hot.CheckpointId) > 0;
        var cold = open.Skip(1).LastOrDefault(c => c.StaffCount >= 2);
        var second = open.Skip(1).FirstOrDefault(c => queue.GetValueOrDefault(c.CheckpointId) > 0);
        if (queued)
        {
            if (cold is not null)
                candidates.Add(new("move", "move", $"Mută un operator de la {Name(cold.CheckpointId)} la {Name(hot!.CheckpointId)}",
                    new Dictionary<Guid, int> { [cold.CheckpointId] = -1, [hot.CheckpointId] = 1 }));
            candidates.Add(new("add-1", "add", $"Adaugă un operator la {Name(hot!.CheckpointId)}",
                new Dictionary<Guid, int> { [hot.CheckpointId] = 1 }));
            candidates.Add(second is null
                ? new("add-2", "add", $"Adaugă doi operatori la {Name(hot.CheckpointId)}", new Dictionary<Guid, int> { [hot.CheckpointId] = 2 })
                : new("add-2", "add", $"Adaugă câte un operator la {Name(hot.CheckpointId)} și {Name(second.CheckpointId)}",
                    new Dictionary<Guid, int> { [hot.CheckpointId] = 1, [second.CheckpointId] = 1 }));
        }
        // With no queue anywhere the useful question is the opposite one: can the day run with fewer people?
        var spare = queued ? cold : open.LastOrDefault(c => c.StaffCount >= 2);
        if (spare is not null)
            candidates.Add(new("save", "save", $"Retrage un operator de la {Name(spare.CheckpointId)}",
                new Dictionary<Guid, int> { [spare.CheckpointId] = -1 }));
        // Desks beyond the area's count are refused by Staff, which also says why when none can open.
        if (desks is { MaximumQueue: > 0 })
        {
            var more = Enumerable.Range(1, 2).Select(count => new OptimizationCandidate($"desk-{count}", "desk",
                count == 1 ? $"Deschide un ghișeu în plus la {desks.Name}" : $"Deschide două ghișee în plus la {desks.Name}", new Dictionary<Guid, int>(), count));
            // Every control counts here, planned ones too: the bottleneck is where people wait, whoever staffs it.
            var worst = (queue.Values.DefaultIfEmpty().Max(), p95.Values.DefaultIfEmpty().Max());
            candidates.InsertRange((desks.MaximumQueue, desks.P95WaitMinutes ?? 0).CompareTo(worst) > 0 ? 0 : candidates.Count, more);
        }
        return candidates;
    }

    public static SimulationRequest Apply(SimulationRequest request, Guid runId, OptimizationCandidate candidate)
    {
        var checkpoints = request.Checkpoints.Select(c => c with { StaffCount = c.StaffCount + candidate.StaffDelta.GetValueOrDefault(c.CheckpointId) }).ToArray();
        // The engine requires the passenger generator to see the same checkpoints as the simulation.
        return request with
        {
            RunId = runId, Checkpoints = checkpoints,
            PassengerGeneration = request.PassengerGeneration is { } generation ? generation with { Checkpoints = checkpoints } : null,
            CheckIn = request.CheckIn is { } checkIn && candidate.DeskDelta != 0 ? checkIn with { StaffCount = checkIn.StaffCount + candidate.DeskDelta } : request.CheckIn
        };
    }

    /// <summary>
    /// An option written into the scenario's people, with only the people the scenario has (the what-if rule). A removal
    /// frees the last person allocated at that control and a move re-points that same person to the target. An addition
    /// takes someone allocated nowhere in the same terminal: one of the scenario's unassigned security operators, or, in a
    /// scenario without its own team, a free layout slot. An open desk needs a check-in operator not yet at a desk, within
    /// the area's desks; without a team the desks are just a count. Inputs is null when Issue says, in Romanian, why not.
    /// </summary>
    public static (ScenarioInputs? Inputs, string? Issue) Staff(ScenarioInputs inputs, SceneDetails layout, IReadOnlyDictionary<Guid, int> delta, int desks = 0)
    {
        if (delta.Count > 0 && inputs.StaffShifts is not null) return (null, "Scenariul folosește un program pe ture; modifică turele din Scenario Studio.");
        var checkpoints = layout.Checkpoints.ToDictionary(c => c.Id);
        if (delta.Keys.Any(id => !checkpoints.ContainsKey(id))) return (null, "Controlul nu mai există în layout-ul scenariului.");
        string Name(Guid id) => string.IsNullOrWhiteSpace(checkpoints[id].Code) ? checkpoints[id].Name : checkpoints[id].Code;
        var terminals = layout.Staff.ToDictionary(m => m.Id, m => m.TerminalId);
        var allocations = inputs.StaffAllocations.ToList();
        var pool = Pool(inputs, layout);
        var freed = new List<Guid>();
        // Removals first, so a move hands the donor's person to the target.
        foreach (var (id, count) in delta.Where(d => d.Value < 0).OrderBy(d => d.Key))
            for (var i = 0; i < -count; i++)
            {
                var row = allocations.LastOrDefault(a => a.CheckpointId == id);
                if (row is null) return (null, $"La {Name(id)} nu mai este niciun operator de retras.");
                allocations.Remove(row);
                freed.Add(row.StaffMemberId);
            }
        foreach (var (id, count) in delta.Where(d => d.Value > 0).OrderBy(d => d.Key))
            for (var i = 0; i < count; i++)
            {
                bool Free(Guid person) => terminals.TryGetValue(person, out var terminal) && terminal == checkpoints[id].TerminalId
                    && allocations.All(a => a.StaffMemberId != person);
                var person = freed.Concat(pool).FirstOrDefault(Free);
                if (person == Guid.Empty)
                    return (null, $"Nu există niciun operator de securitate liber pentru {Name(id)}: toți oamenii scenariului sunt deja alocați.");
                allocations.Add(new(person, id));
            }
        var result = inputs with { StaffAllocations = allocations };
        if (desks == 0) return (result, null);
        if (inputs.CheckIn is not { } checkIn || layout.CheckInAreas?.SingleOrDefault(a => a.Id == checkIn.AreaId) is not { } area)
            return (null, "Scenariul nu are check-in.");
        var place = string.IsNullOrWhiteSpace(area.Code) ? area.Name : area.Code;
        var target = checkIn.StaffCount + desks;
        if (target < 1) return (null, $"La {place} trebuie să rămână cel puțin un ghișeu deschis.");
        if (target > area.DeskCount) return (null, $"La {place} sunt deja deschise {checkIn.StaffCount} din cele {area.DeskCount} ghișee.");
        if (inputs.Operators?.Count(p => p.Role == "checkIn") is { } people && people < target)
            return (null, $"Toți cei {people} operatori de check-in ai scenariului sunt deja la ghișee; pentru mai multe ghișee adaugă operatori de check-in în Scenario Studio.");
        return (result with { CheckIn = checkIn with { StaffCount = target }, Operators = OperatorScenarios.WithDesks(inputs.Operators, target) }, null);
    }

    // A scenario with its own team staffs controls only with its security operators, never the layout's spare slots.
    private static IEnumerable<Guid> Pool(ScenarioInputs inputs, SceneDetails layout)
    {
        var team = inputs.Operators?.Where(p => p.Role == "security").Select(p => p.Id).ToHashSet();
        return layout.Staff.Select(m => m.Id).Where(id => team is null || team.Contains(id)).Order();
    }

    public static IReadOnlyList<OptimizationControl> Controls(IReadOnlyList<CheckpointInput> checkpoints, IReadOnlyList<KpiSnapshot> baseline,
        IReadOnlyDictionary<Guid, string> names) =>
        checkpoints.Select(c =>
        {
            var zone = baseline.Where(s => s.ZoneId == c.CheckpointId).ToArray();
            return new OptimizationControl(names.GetValueOrDefault(c.CheckpointId, "control"), c.StaffCount,
                zone.MaxBy(s => s.Minute)?.P95WaitMinutes, zone.Length == 0 ? 0 : zone.Max(s => s.QueuedPassengers));
        }).ToArray();

    /// <summary>
    /// Passengers first: among options that shorten the mean wait by at least 5%, the one costing the fewest extra people
    /// while staying within 5% of the best wait (so a free move beats hiring when it is nearly as good). Only when nothing
    /// shortens the wait does a saving win: one operator fewer with at most 5% more wait. Nothing qualifies: optimal.
    /// </summary>
    public static string? Recommend(AgentImpactSide baseline, IReadOnlyList<(OptimizationCandidate Candidate, AgentImpactSide Result)> results)
    {
        var better = results.Where(r => Shortens(baseline, r)).ToArray();
        if (better.Length > 0)
        {
            var best = better.Min(r => r.Result.MeanWaitMinutes!.Value);
            return better.Where(r => r.Result.MeanWaitMinutes <= best * 1.05).OrderBy(r => r.Candidate.OperatorDelta)
                .ThenBy(r => r.Result.MeanWaitMinutes).First().Candidate.Id;
        }
        return results.Where(r => Saves(baseline, r)).OrderBy(r => r.Result.MeanWaitMinutes).ThenBy(r => r.Candidate.OperatorDelta)
            .Select(r => r.Candidate.Id).FirstOrDefault();
    }

    private static bool Shortens(AgentImpactSide baseline, (OptimizationCandidate Candidate, AgentImpactSide Result) r) =>
        baseline.MeanWaitMinutes is { } reference && r.Result.MeanWaitMinutes < reference * 0.95;

    // Without a reference wait nothing can be compared, so nothing is worth recommending.
    private static bool Saves(AgentImpactSide baseline, (OptimizationCandidate Candidate, AgentImpactSide Result) r) =>
        baseline.MeanWaitMinutes is { } reference && r.Candidate.OperatorDelta < 0 && r.Result.MeanWaitMinutes <= reference * SavingTolerance;
    // A saving may lengthen waits by at most 5%; undoing it then shortens them by under 5% (1/1.05 > 0.95), which is not
    // worth recommending, so save and add-back never alternate run after run.
    private const double SavingTolerance = 1.05;
    private static bool Worth(AgentImpactSide baseline, (OptimizationCandidate Candidate, AgentImpactSide Result) r) =>
        Saves(baseline, r) || Shortens(baseline, r);

    /// <summary>
    /// What code decides before the model: each option's digested facts and verdict, the bottleneck, why the option picked
    /// by <see cref="Recommend"/> is recommended, and a summary that needs no model.
    /// </summary>
    public static OptimizationBrief Brief(AgentImpactSide baseline, IReadOnlyList<(OptimizationCandidate Candidate, AgentImpactSide Result)> results,
        OptimizationPeak? bottleneck)
    {
        var recommended = Recommend(baseline, results);
        var facts = results.Select(r => Facts(baseline, r, r.Candidate.Id == recommended)).ToArray();
        var pick = results.FirstOrDefault(r => r.Candidate.Id == recommended);
        var where = bottleneck is null ? "Nu s-a format nicio coadă."
            : $"Cea mai lungă coadă: {OptimizationText.People(bottleneck.Queued)} la {bottleneck.Place}, în minutul {bottleneck.Minute}.";
        if (recommended is null) return new(bottleneck, null, facts, where, "Nicio variantă testată nu scurtează vizibil așteptarea.");
        var shortest = results.Where(r => Shortens(baseline, r)).Select(r => r.Result.MeanWaitMinutes).DefaultIfEmpty().Min();
        var why = !Shortens(baseline, pick) ? "economisește personal fără să lungească vizibil așteptarea"
            : pick.Result.MeanWaitMinutes <= shortest ? "scurtează cel mai mult așteptarea medie dintre variantele testate"
            : "scurtează așteptarea aproape cât cea mai bună variantă, cu mai puțin personal";
        var chosen = facts.Single(f => f.Recommended);
        return new(bottleneck, $"{chosen.Action}: {why}.", facts, where, $"Recomandăm „{chosen.Action}”: {char.ToLowerInvariant(chosen.Facts[0])}{chosen.Facts[1..]}");
    }

    private static OptimizationFacts Facts(AgentImpactSide baseline, (OptimizationCandidate Candidate, AgentImpactSide Result) r, bool recommended)
    {
        var (c, before, after) = (r.Candidate, baseline.MeanWaitMinutes, r.Result.MeanWaitMinutes);
        var verdict = OptimizationText.Verdict(before, after);
        var wait = before is { } b && after is { } a
            ? $"{OptimizationText.Duration(b)} → {OptimizationText.Duration(a)}" + (b > 0 ? $" ({Math.Round((a - b) / b * 100):+0;-0;0}%)" : "") : "necunoscută";
        // shortcut: a change under half a second still reads "1 s"; only tiny waits get there, and the verdict already needs 5%.
        var change = before is { } b2 && after is { } a2 ? OptimizationText.Duration(Math.Max(Math.Abs(a2 - b2), 1 / 60d)) : null;
        var extra = c.Kind == "desk" ? (c.DeskDelta == 1 ? "un ghișeu de check-in" : c.DeskDelta == 2 ? "două ghișee de check-in" : $"{c.DeskDelta} ghișee de check-in")
            : OptimizationText.Operators(c.OperatorDelta);
        var saved = c.OperatorDelta == -1 ? "se economisește un operator" : $"se economisesc {OptimizationText.Operators(-c.OperatorDelta)}";
        var sentence = verdict switch
        {
            "better" => $"Pasagerii așteaptă în medie cu {change} mai puțin",
            "worse" => $"Pasagerii așteaptă în medie cu {change} mai mult",
            _ => change is null ? "Așteptarea medie nu poate fi comparată" : "Așteptarea medie rămâne practic aceeași"
        } + c.OperatorDelta switch
        {
            > 0 => verdict == "better" ? $", cu {extra} în plus." : $", chiar cu {extra} în plus.",
            0 => ", fără personal în plus.",
            _ => verdict == "worse" ? $", dar {saved}." : $" și {saved}."
        };
        var staff = c.OperatorDelta switch { > 0 => $"{extra} în plus", 0 => "fără personal în plus", _ => saved };
        var p95 = baseline.MaxP95WaitMinutes is { } p && r.Result.MaxP95WaitMinutes is { } q ? $"{OptimizationText.Duration(p)} → {OptimizationText.Duration(q)}" : null;
        return new(c.Id, c.Action, recommended, verdict, wait, $"{baseline.MaxQueue} → {r.Result.MaxQueue}", p95, staff, sentence);
    }

    /// <summary>
    /// The panel: the recommendation is the brief's, never the model's; each card is titled by its action. The model's
    /// sentences are kept only when <see cref="OptimizationText"/> finds them consistent with the numbers, otherwise the
    /// brief's own sentences stand. Places are every zone name the model could wrongly mention.
    /// </summary>
    public static RunOptimization Compose(AgentImpactSide baseline, IReadOnlyList<OptimizationControl> controls,
        IReadOnlyList<(OptimizationCandidate Candidate, AgentImpactSide Result)> results, OptimizationBrief brief, OptimizationAdvice advice,
        IReadOnlyCollection<string> places)
    {
        var answered = advice.Status == "chosen";
        var written = (answered ? advice.Options ?? [] : []).GroupBy(o => o.Id).ToDictionary(g => g.Key, g => g.First().Explanation);
        var queues = results.ToDictionary(r => r.Candidate.Id, r => Math.Sign(r.Result.MaxQueue - baseline.MaxQueue));
        var deltas = results.ToDictionary(r => r.Candidate.Id, r => r.Candidate.OperatorDelta);
        bool Agrees(string? text, OptimizationFacts facts) =>
            OptimizationText.Agrees(text, facts, queues[facts.Id], deltas[facts.Id], [facts.Action, brief.Bottleneck?.Place ?? ""], places);
        var chosen = brief.Options.FirstOrDefault(o => o.Recommended);
        // The advice must name the recommended action as written; the rest of it is held to that option's numbers.
        var advised = answered && chosen is not null && advice.Advice is { } text && OptimizationText.Names(text, chosen.Action)
            && Agrees(OptimizationText.Without(text, chosen.Action), chosen) ? text : brief.Advice;
        return new(advice.Status, advice.Model, $"{brief.Where} {advised}", baseline, controls, results.Select(r =>
        {
            var fact = brief.Options.Single(f => f.Id == r.Candidate.Id);
            var own = written.GetValueOrDefault(r.Candidate.Id);
            return new RunOptimizationOption(r.Candidate.Id, r.Candidate.Kind, r.Candidate.Action, r.Candidate.OperatorDelta, r.Result,
                Agrees(own, fact) ? own! : fact.Facts, fact.Recommended, r.Candidate.StaffDelta, r.Candidate.DeskDelta);
        }).OrderByDescending(o => o.Recommended).ToArray(), Optimal: results.Count > 0 && brief.Recommendation is null);
    }
}

public static class OptimizationBottleneck
{
    /// <summary>
    /// The single longest queue at any minute, check-in desks included, earliest minute on a tie: the same "busiest moment"
    /// the run summary's headline names. Computed here so a small local model never has to infer it from the numbers.
    /// </summary>
    public static OptimizationPeak? Of(IReadOnlyList<KpiSnapshot> controls, IReadOnlyList<CheckInSnapshot>? checkIn, IReadOnlyDictionary<Guid, string> names) =>
        controls.Select(s => (s.ZoneId, s.Minute, s.QueuedPassengers)).Concat((checkIn ?? []).Select(s => (s.ZoneId, s.Minute, s.QueuedPassengers)))
            .Where(s => s.QueuedPassengers > 0).OrderByDescending(s => s.QueuedPassengers).ThenBy(s => s.Minute)
            .Select(s => new OptimizationPeak(names.GetValueOrDefault(s.ZoneId, "control"), s.QueuedPassengers, s.Minute)).FirstOrDefault();
}

/// <summary>
/// Romanian wording shared by the deterministic text and the checks on the model's text. The checks are deliberately
/// strict: a sentence they wrongly reject only costs the model's phrasing, since the deterministic sentence replaces it.
/// </summary>
public static class OptimizationText
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private const RegexOptions Plain = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    // The checks read text folded to plain letters, so "așteaptă", "aşteaptă" and "asteapta" are one word; "cel mai mult" is no comparison.
    private static readonly Regex Shorter = new(@"\b(scad|scurt|(?<!cel )mai (putin|rapid|repede)|reduc|redus|imbunatat|accelere)", Plain);
    private static readonly Regex Longer = new(@"\b(cres|(?<!cel )mai (lung|mult|mare)|lungeste|prelung|inrautat|intarzi)", Plain);
    private static readonly Regex Same = new(@"\b(la fel|aceeasi|acelasi|neschimbat|practic|aproape|similar|stabil)", Plain);
    // "nu scade", "fără să crească": a negated change says the wait stays, whatever the verb.
    private static readonly Regex Negated = new(@"\b(nu|fara sa|fara a)\s+(se\s+)?\w+(\s+(vizibil|semnificativ|clar|mult))?", Plain);
    // Staff amounts use the same comparatives as waits ("mai puțin personal"), so they are left out of the wait checks.
    private static readonly Regex StaffAmount = new(@"\b(mai (putini?|multi?)|reduc\w*|redus\w*|cres\w*)\s+(de\s+)?(personal|operator|oameni|angajat|ghise|numarul)\w*", Plain);
    private static readonly Regex MoreStaff = new(@"\b(in plus|suplimentar|adaug)", Plain);
    private static readonly Regex LessStaff = new(@"\b(economis|retras|retrag|mai putin personal|mai putini operatori)", Plain);
    private static readonly Regex Clauses = new(@"[,.;:!?]|\b(dar|iar|insa|desi|si|pentru ca|deoarece|ceea ce)\b", Plain);
    // "așteaptă" and "așteptare" fold to different stems.
    private static readonly Regex Waiting = new(@"\b(asteap|astept|timp)", Plain);
    private static readonly Regex Queueing = new(@"\b(coad|cozi)", Plain);
    private static readonly Regex Helps = new(@"(?<!\bnu )\bajut", Plain);
    private static readonly Regex Number = new(@"\d+(?:[.,]\d+)?");

    public static string Verdict(double? before, double? after) => before is not { } b || after is not { } a ? "same"
        : a < b * 0.95 ? "better" : a > b * 1.05 ? "worse" : "same";

    /// <summary>Like the panel's own numbers: seconds under a minute, otherwise minutes with a decimal comma.</summary>
    public static string Duration(double minutes) => Math.Round(minutes * 60) < 60
        ? $"{Math.Round(minutes * 60).ToString(Invariant)} s" : $"{Math.Round(minutes, 1).ToString("0.#", Invariant).Replace('.', ',')} min";

    public static string People(int count) => count == 1 ? "un pasager" : $"{count}{Of(count)} pasageri";
    public static string Operators(int count) => count switch { 1 => "un operator", 2 => "doi operatori", _ => $"{count}{Of(count)} operatori" };
    // Romanian puts "de" between a number from 20 up (except 101-119 and so on) and its noun.
    private static string Of(int count) => count >= 20 && count % 100 is 0 or >= 20 ? " de" : "";

    /// <summary>
    /// A sentence agrees with an option when it names only the allowed places and no number (the card shows them), calls
    /// only the recommended option recommended, says what happens to the wait as the verdict does, never moves the wait or
    /// the longest queue the other way, and never claims staff the other way than the option's operator change.
    /// </summary>
    public static bool Agrees(string? text, OptimizationFacts facts, int queueChange, int staffChange, IReadOnlyCollection<string> allowed, IReadOnlyCollection<string> places)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var mentioned = places.Where(place => Names(text, place)).ToArray();
        if (mentioned.Any(place => !allowed.Any(given => Names(given, place)))) return false;
        var plain = Fold(mentioned.Aggregate(text, Without));
        var wait = facts.Verdict switch { "better" => -1, "worse" => 1, _ => 0 };
        if (Number.IsMatch(plain) || (!facts.Recommended && plain.Contains("recomand", StringComparison.OrdinalIgnoreCase))
            || (wait > 0 && Helps.IsMatch(plain)) || (wait < 0 && plain.Contains("nu ajut", StringComparison.OrdinalIgnoreCase))) return false;
        var stated = false;
        foreach (var clause in Clauses.Split(plain))
        {
            if (!Regex.IsMatch(clause, @"\b(nu|fara)\b", Plain) && ((staffChange > 0 && LessStaff.IsMatch(clause))
                || (staffChange == 0 && (LessStaff.IsMatch(clause) || MoreStaff.IsMatch(clause))) || (staffChange < 0 && MoreStaff.IsMatch(clause)))) return false;
            var change = Negated.Replace(StaffAmount.Replace(clause, " "), " la fel ");
            var about = Waiting.IsMatch(change) ? "wait" : Queueing.IsMatch(change) ? "queue" : null;
            if (about is null) continue;
            var trend = about == "wait" ? wait : queueChange;
            if ((trend >= 0 && Shorter.IsMatch(change)) || (trend <= 0 && Longer.IsMatch(change))) return false;
            stated |= about == "wait" && (wait < 0 ? Shorter : wait > 0 ? Longer : Same).IsMatch(change);
        }
        return stated;
    }

    /// <summary>Whether the text names this place or action, as a whole word and ignoring diacritics.</summary>
    public static bool Names(string text, string name) => Regex.IsMatch(Fold(text), Whole(Fold(name)), Plain);

    public static string Without(string text, string name) => Regex.Replace(Fold(text), Whole(Fold(name)), " ", Plain);

    private static string Whole(string name) => $@"(?<![\w-]){Regex.Escape(name)}(?![\w-])";

    private static string Fold(string text) => new(text.Normalize(NormalizationForm.FormD)
        .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
}

public static class OptimizationPrompts
{
    // Tuned against qwen2.5:7b on real runs: it copies examples and field names, so the prompt has neither, and every
    // sentence it writes is still checked by OptimizationText before it is shown.
    public const string System = """
        You write short texts in Romanian for a non-technical airport manager about staffing options already tested in a
        simulator of a synthetic demo airport. The numbers, each option's verdict and the recommended option are computed in
        code and are correct. Only put them into plain words: never pick another option, never invent places, causes or numbers.

        "options": for every option, "explanation" is one short sentence (at most 15 words, no digits) in two parts:
        first what happens to the passengers' wait, matching its verdict (better: they wait less; worse: they wait more;
        same: the wait stays practically the same), then what it costs in staff, from its "staff".

        "advice": one or two short sentences for the manager, no digits. If recommendation is not null, start with the
        recommended option's action copied exactly, then say in a few plain words why it is the best choice for passengers and staff.
        If recommendation is null, say that no tested option clearly shortens the wait, so the staffing can stay as it is.

        Plain Romanian with diacritics, no markdown, no field names.
        """;
}
