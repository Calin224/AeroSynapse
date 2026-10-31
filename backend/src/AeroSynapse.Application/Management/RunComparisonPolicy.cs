using System.Text.Json;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.DigitalTwin;

namespace AeroSynapse.Application.Management;

public static class RunComparisonPolicy
{
    // Staffing and navigation affect the journey, not the initial generated population.
    // The remaining fields include all generator inputs, including compiled incident surges.
    public static string PopulationHash(SimulationRequest request) => RunFingerprint.Hash(JsonSerializer.SerializeToElement(new
    {
        populationVersion = "generation-inputs-v1", engine = RunFingerprint.Engine(request), request.StartsAtUtc,
        request.TimeZone, request.DurationMinutes, request.Seed, request.PassengerCount, request.ArrivalRatePerMinute,
        Connections = request.Journey?.Connections,
        Checkpoints = request.Checkpoints.Select(c => c with { StaffCount = 0 }).ToArray(),
        PassengerGeneration = request.PassengerGeneration is { } generation
            ? generation with { Checkpoints = generation.Checkpoints.Select(c => c with { StaffCount = 0 }).ToArray() } : null
    }, ContractJson.Options), "population");

    public static RunComparability Assess(SimulationRun baseline, SimulationRun candidate)
    {
        var issues = new List<ComparisonIssue>();
        void Issue(string code, string message) => issues.Add(new(code, message));
        if (baseline.Id == candidate.Id) Issue("same_run", "Alege două runId-uri distincte; o rulare nu poate fi propriul candidat.");
        if (!baseline.ResultsValid || !candidate.ResultsValid) Issue("results_unavailable", "Ambele rulări trebuie să fie completed, cu rezultate valide.");
        if (baseline.EngineVersion is null || candidate.EngineVersion is null || baseline.EngineBuild is null || candidate.EngineBuild is null)
            Issue("engine_unknown", "Versiunea sau build-ul motorului lipsesc; compatibilitatea rezultatelor nu poate fi confirmată.");
        else if (baseline.EngineVersion != candidate.EngineVersion || baseline.EngineBuild != candidate.EngineBuild)
            Issue("engine_mismatch", "Rulările folosesc versiuni sau build-uri diferite ale motorului.");
        var left = Captured(baseline); var right = Captured(candidate);
        string? leftHash = null, rightHash = null;
        if (left is null || right is null) Issue("inputs_unavailable", "Lipsesc intrări capturate valide; populația și intervalul nu pot fi verificate.");
        else
        {
            leftHash = PopulationHash(left); rightHash = PopulationHash(right);
            if (baseline.SceneId != candidate.SceneId || left.LayoutVersionId != right.LayoutVersionId)
                Issue("layout_mismatch", "Aeroportul sau versiunea layout-ului diferă; zonele nu pot fi aliniate în siguranță.");
            if (left.StartsAtUtc != right.StartsAtUtc || left.DurationMinutes != right.DurationMinutes || left.TimeZone != right.TimeZone
                || left.SnapshotIntervalMinutes != right.SnapshotIntervalMinutes)
                Issue("observation_mismatch", "Începutul UTC, fusul orar, orizontul sau ferestrele de măsurare diferă.");
            if (left.Seed != right.Seed) Issue("seed_mismatch", "Seed-uri diferite: această pereche nu controlează aceeași populație inițială.");
            if (leftHash != rightHash) Issue("population_mismatch", "Intrările care generează populația inițială diferă. Nu atribui diferențele unui singur parametru operațional.");
        }
        return new(issues.Count == 0, leftHash is null || rightHash is null ? null : leftHash == rightHash, leftHash, rightHash, issues);
    }

    private static SimulationRequest? Captured(SimulationRun run)
    {
        if (run.InputSnapshotJson is null) return null;
        try
        {
            var snapshot = JsonSerializer.Deserialize<RunInputSnapshot>(run.InputSnapshotJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (snapshot?.SchemaVersion != 1 || snapshot.Scenario?.Inputs is null || snapshot.Scenario.Scenario is null || snapshot.Layout?.Layout is null || snapshot.Layout.Version is null) return null;
            var scenario = snapshot.Scenario;
            var layout = snapshot.Layout.Layout;
            if (layout.Scene is null || layout.Checkpoints?.All(c => c is not null) != true || layout.Gates?.All(g => g is not null) != true
                || layout.Staff?.All(s => s is not null) != true || layout.Routes?.All(r => r is not null) != true) return null;
            ScenarioValidator.Validate(new(scenario.Scenario.Code, scenario.Scenario.Name, scenario.Scenario.Description,
                scenario.Scenario.SceneId, scenario.Scenario.LayoutVersionId, scenario.Inputs), layout);
            return CapturedSimulation.Request(run.Id, snapshot);
        }
        catch (Exception error) when (error is JsonException or ScenarioValidationException or ContractValidationException or ArgumentException or InvalidOperationException)
        { return null; }
    }

    public static MetricDifference Difference(Guid zoneId, string name, string unit, RunMetric? baseline, RunMetric? candidate, bool comparable, string zoneType = "checkpoint")
    {
        decimal? delta = null, percent = null; string? reason;
        if (!comparable) reason = "runs_not_comparable";
        else if (baseline is null || candidate is null) reason = "metric_unavailable";
        else if (baseline.RecordedAtUtc != candidate.RecordedAtUtc) reason = "observation_mismatch";
        else
        {
            reason = null;
            try { delta = candidate.Value - baseline.Value; }
            catch (OverflowException) { reason = "difference_out_of_range"; }
            if (reason is null)
            {
                if (baseline.Value <= 0) reason = "baseline_not_positive";
                else try { percent = delta / baseline.Value * 100; }
                    catch (OverflowException) { reason = "percentage_out_of_range"; }
            }
        }
        return new(zoneId, name, unit, baseline?.Value, candidate?.Value, delta, percent, reason, zoneType,
            baseline?.RecordedAtUtc, candidate?.RecordedAtUtc);
    }
}
