using AeroSynapse.Application.Contracts;

namespace AeroSynapse.Application.Management;

public sealed record ParetoPoint(Guid RunId, int Position, bool Feasible, IReadOnlyList<string> Exclusions,
    bool OnFrontier, IReadOnlyList<Guid> DominatedBy, double? TimeMinutes, long CostPoints, double? RiskFraction,
    double? NormalizedTime, double NormalizedCost, double? NormalizedRisk, double? WeightedScore);
public sealed record ParetoTradeoff(Guid FirstRunId, Guid SecondRunId, double TimeDeltaMinutes, long CostDeltaPoints, double RiskDeltaFraction);
public sealed record ParetoAnalysis(string Status, ParetoOptions Options, string Formula, ParetoSearchResult Planning,
    IReadOnlyList<ParetoPoint> Points, IReadOnlyList<Guid> FrontierRunIds, Guid? PreferredRunId,
    IReadOnlyList<ParetoTradeoff> Tradeoffs, IReadOnlyList<string> Assumptions);
public sealed record ParetoCrossComparison(Guid LeftRunId, Guid RightRunId, string Relation);
public sealed record ParetoFrontierComparison(bool Comparable, IReadOnlyList<string> Issues, ParetoAnalysis Left,
    ParetoAnalysis Right, IReadOnlyList<Guid> UnionFrontierRunIds, IReadOnlyList<ParetoCrossComparison> Comparisons);

public static class ParetoAnalysisPolicy
{
    public const string Formula = "S = (wT*(meanWaitMinutes/Tref) + wC*(operatingPoints/Cref) + wR*(boardingRiskFraction/Rref)) / (wT+wC+wR); minimize";
    public static bool Dominates(ParetoPoint first, ParetoPoint second) => first.Feasible && second.Feasible
        && first.TimeMinutes <= second.TimeMinutes + 1e-9 && first.CostPoints <= second.CostPoints && first.RiskFraction <= second.RiskFraction + 1e-9
        && (first.TimeMinutes < second.TimeMinutes - 1e-9 || first.CostPoints < second.CostPoints || first.RiskFraction < second.RiskFraction - 1e-9);

    public static ParetoAnalysis Analyze(StaffingEvaluationDetails evaluation, ParetoSearchResult planning)
    {
        var request = planning.Request; var options = request.Options; var n = options.Normalization; var w = options.Weights;
        var ready = evaluation.Batch.Batch.Status is "completed" or "completed_with_failures";
        var constraints = request.Constraints.Checkpoints.ToDictionary(item => item.CheckpointId);
        var points = evaluation.Variants.Select(variant =>
        {
            var reasons = new List<string>(); var counts = variant.Configuration.ToDictionary(item => item.CheckpointId, item => item.StaffCount);
            var metrics = variant.Metrics;
            if (!ready) reasons.Add("evaluation_incomplete_or_cancelled");
            if (variant.RunStatus != "completed" || metrics?.MeanWaitMinutes is null || metrics.P95WaitMinutes is null || metrics.BoardingRisk is null)
                reasons.Add("missing_successful_observations");
            var tolerances = new[] { "mean_wait_degradation", "p95_degradation", "boarding_risk_degradation", "unprocessed_degradation", "reallocation_cost_limit" };
            if (variant.Assessment.Status is "rejected" or "unassessable"
                && variant.Assessment.Reasons.Any(code => !tolerances.Contains(code.Split(':')[0])))
                reasons.Add("incomparable_population_or_engine");
            if (!counts.Keys.ToHashSet().SetEquals(constraints.Keys)) reasons.Add("checkpoint_set_mismatch");
            long cost = 0;
            foreach (var cp in constraints.Values)
            {
                var count = counts.GetValueOrDefault(cp.CheckpointId);
                cost += ParetoModel.Cost(cp, count, request.DurationMinutes, options);
                if (count < 0 || count > Math.Min(cp.PhysicalLimit, cp.MaximumStaff) || cp.Closed && count != 0
                    || cp.RequiredOpen && count < Math.Max(1, cp.MinimumStaff) || count > 0 && count < cp.MinimumStaff)
                    reasons.Add("staffing_constraint:" + cp.CheckpointId);
            }
            if (counts.Values.Sum() > request.Constraints.AvailableStaff || request.Constraints.StaffPools.Any(pool =>
                constraints.Values.Where(cp => cp.TerminalId == pool.TerminalId).Sum(cp => counts.GetValueOrDefault(cp.CheckpointId)) > pool.AvailableStaff))
                reasons.Add("staff_budget");
            if (cost > options.OperationalBudget) reasons.Add("operational_budget");
            if (metrics?.MeanWaitMinutes > options.MaxMeanWaitMinutes + 1e-9) reasons.Add("mean_wait_limit");
            if (metrics?.P95WaitMinutes > options.MaxP95WaitMinutes + 1e-9) reasons.Add("p95_limit");
            if (metrics?.BoardingRisk > options.MaxBoardingRisk + 1e-9) reasons.Add("boarding_risk_limit");
            var t = metrics?.MeanWaitMinutes / n.TimeMinutes; var c = cost / n.CostPoints; var r = metrics?.BoardingRisk / n.RiskFraction;
            var score = reasons.Count == 0 ? (w.Time * t + w.Cost * c + w.Risk * r) / (w.Time + w.Cost + w.Risk) : null;
            return new ParetoPoint(variant.RunId, variant.Position, reasons.Count == 0, reasons, false, [], metrics?.MeanWaitMinutes,
                cost, metrics?.BoardingRisk, t, c, r, score);
        }).ToArray();
        points = points.Select(point => { var dominated = points.Where(other => Dominates(other, point)).Select(other => other.RunId).ToArray();
            return point with { OnFrontier = point.Feasible && dominated.Length == 0, DominatedBy = dominated }; }).ToArray();
        var frontier = points.Where(point => point.OnFrontier).ToArray();
        var tradeoffs = new List<ParetoTradeoff>();
        for (var i = 0; i < frontier.Length; i++)
            for (var j = i + 1; j < frontier.Length; j++)
            {
                var a = frontier[i]; var b = frontier[j];
                var dt = b.TimeMinutes!.Value - a.TimeMinutes!.Value; var dc = b.CostPoints - a.CostPoints; var dr = b.RiskFraction!.Value - a.RiskFraction!.Value;
                if (new[] { dt, dc, dr }.Any(delta => delta > 1e-9) && new[] { dt, dc, dr }.Any(delta => delta < -1e-9))
                    tradeoffs.Add(new(a.RunId, b.RunId, dt, dc, dr));
            }
        var status = !ready ? "unavailable" : frontier.Length == 0 ? "no_feasible_solution" : tradeoffs.Count == 0 ? "no_tradeoff_observed" : "tradeoffs_available";
        return new(status, options, Formula, planning, points, frontier.Select(point => point.RunId).ToArray(),
            frontier.OrderBy(point => point.WeightedScore).ThenBy(point => point.Position).FirstOrDefault()?.RunId, tradeoffs,
            ["Frontieră aproximativă: baseline și maximum patru configurații distincte, nu enumerarea întregului spațiu fezabil.",
             "Scorul final folosește așteptarea medie SimPy (minute), costul operațional (puncte) și riscul de boarding (fracție 0–1). Toate se minimizează.",
             "Normalizare prin referințe pozitive fixe, fără min-max și fără limitare la 1; ponderile sunt împărțite la suma lor.",
             "Cost = operatori × durata întregii simulări × puncte/operator/minut + costul adăugărilor și retragerilor. Nu reprezintă bani.",
             "Limitele de waiting/P95/risc sunt verificate după simulare; limitele fizice, personalul și bugetul sunt impuse și în CP-SAT.",
             "Generarea folosește proxy-uri de presiune și deficit; acestea diferă de metricile SimPy și nu garantează optimul scorului final.",
             "Aceeași populație, seed și durată pentru fiecare variantă; o singură replicație nu estimează incertitudinea statistică.",
             "Schimbarea ponderilor lansează rezolvări CP-SAT și simulări noi; soluțiile pot rămâne identice pe intervale de ponderi.",
             "Nicio alegere de pe frontieră nu modifică automat scenariul sau echipamentele reale."]);
    }

    public static ParetoFrontierComparison Compare(StaffingEvaluationDetails left, StaffingEvaluationDetails right)
    {
        var a = left.Pareto!; var b = right.Pareto!; var issues = new List<string>();
        var l = a.Planning.Request; var r = b.Planning.Request;
        if (l.Constraints.ScenarioId != r.Constraints.ScenarioId || l.Constraints.ScenarioRevision != r.Constraints.ScenarioRevision
            || l.DurationMinutes != r.DurationMinutes || !left.Batch.Seeds.SequenceEqual(right.Batch.Seeds)
            || left.Batch.Batch.EngineBuild != right.Batch.Batch.EngineBuild) issues.Add("baseline_revision_seed_or_engine_differs");
        if (a.Status == "unavailable" || b.Status == "unavailable") issues.Add("evaluation_incomplete");
        if (a.Options.StaffMinuteCost != b.Options.StaffMinuteCost
            || !l.Constraints.Checkpoints.OrderBy(cp => cp.CheckpointId).Select(cp => (cp.CheckpointId, cp.ChangeCost))
                .SequenceEqual(r.Constraints.Checkpoints.OrderBy(cp => cp.CheckpointId).Select(cp => (cp.CheckpointId, cp.ChangeCost))))
            issues.Add("cost_model_differs");
        var combined = a.Points.Concat(b.Points).Where(point => point.OnFrontier).ToArray();
        var relations = a.Points.Where(point => point.OnFrontier).SelectMany(first => b.Points.Where(point => point.OnFrontier)
            .Select(second => new ParetoCrossComparison(first.RunId, second.RunId, Dominates(first, second) ? "left_dominates"
                : Dominates(second, first) ? "right_dominates" : Math.Abs(first.TimeMinutes!.Value - second.TimeMinutes!.Value) <= 1e-9
                    && first.CostPoints == second.CostPoints && Math.Abs(first.RiskFraction!.Value - second.RiskFraction!.Value) <= 1e-9 ? "equivalent" : "tradeoff"))).ToArray();
        return new(issues.Count == 0, issues, a, b,
            issues.Count == 0 ? combined.Where(point => !combined.Any(other => Dominates(other, point))).Select(point => point.RunId).Distinct().ToArray() : [],
            issues.Count == 0 ? relations : []);
    }
}
