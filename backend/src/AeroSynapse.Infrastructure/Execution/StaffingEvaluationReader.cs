using System.Data;
using System.Text.Json;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Management;
using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.DigitalTwin;
using AeroSynapse.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace AeroSynapse.Infrastructure.Execution;

internal sealed class StaffingEvaluationReader(TwinDbContext db)
{
    public async Task<StaffingEvaluationDetails> GetAsync(Guid id, CancellationToken token)
    {
        await using var transaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token) : null;
        var stored = await db.Batches.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, token)
            ?? throw new ScenarioNotFoundException("Staffing evaluation was not found.");
        using var document = JsonDocument.Parse(stored.RequestJson);
        var manifest = document.RootElement;
        if (!manifest.TryGetProperty("kind", out var kind) || kind.GetString() != "staffing-evaluation/v1")
            throw new ScenarioNotFoundException("This batch is not a staffing evaluation.");
        var request = manifest.GetProperty("command").GetProperty("request").Deserialize<CreateStaffingEvaluationRequest>(ContractJson.Options)!;
        var planning = ContractJson.Read<StaffingCandidateSearchResult>(manifest.GetProperty("planning"), "StaffingCandidateSearchResult");
        var batch = await new BatchReader(db).ReadAsync(id, token);
        var variants = await db.BatchVariants.AsNoTracking().Where(item => item.BatchId == id).OrderBy(item => item.Position).ToArrayAsync(token);
        var ids = batch.Replications.Select(item => item.RunId).ToArray();
        var runs = await db.Runs.AsNoTracking().Where(item => ids.Contains(item.Id)).ToDictionaryAsync(item => item.Id, token);
        var snapshots = await db.Snapshots.AsNoTracking().Where(item => ids.Contains(item.RunId) && item.ZoneType == "checkpoint")
            .GroupBy(item => new { item.RunId, item.ZoneId }).Select(group => group.OrderByDescending(item => item.RecordedAtUtc).ThenBy(item => item.Id).First()).ToArrayAsync(token);
        var traces = await db.Snapshots.AsNoTracking().Where(item => ids.Contains(item.RunId) && item.ZoneType == "scene").ToArrayAsync(token);
        var baseline = runs[batch.Replications.Single(item => item.VariantId == variants[0].Id).RunId];
        var arrivals = manifest.TryGetProperty("shiftPlanning", out var shiftPlanning) && shiftPlanning.ValueKind == JsonValueKind.Array && shiftPlanning.GetArrayLength() > 0
            ? ContractJson.Read<ShiftPlanResult>(shiftPlanning[0], "ShiftPlanResult").Request.Checkpoints.ToDictionary(item => item.CheckpointId, item => item.ArrivalsPerSlot)
            : new Dictionary<Guid, IReadOnlyList<int>>();
        var original = BatchService.Snapshot(variants[0].InputSnapshotJson).Scenario.Inputs.StaffAllocations.ToDictionary(item => item.StaffMemberId, item => item.CheckpointId);
        var result = new List<StaffingEvaluatedVariant>();
        StaffingSimulatedMetrics? baselineMetrics = null;
        foreach (var variant in variants)
        {
            var replica = batch.Replications.Single(item => item.VariantId == variant.Id);
            var run = runs[replica.RunId]; var captured = BatchService.Snapshot(variant.InputSnapshotJson);
            var allocations = captured.Scenario.Inputs.StaffAllocations.ToDictionary(item => item.StaffMemberId, item => item.CheckpointId);
            var configuration = captured.Layout.Layout.Checkpoints.OrderBy(item => item.Id)
                .Select(item => new StaffingConfiguration(item.Id, item.Name, StaffShiftScenarios.StaffCount(captured.Scenario.Inputs, item.Id))).ToArray();
            var schedule = request.Shifts is null ? null : new StaffingSchedule(StaffShiftScenarios.SlotMinutes,
                captured.Layout.Layout.Checkpoints.OrderBy(item => item.Id)
                    .Select(item => new StaffingScheduleRow(item.Id, item.Name, StaffShiftScenarios.Assigned(captured.Scenario.Inputs, item.Id),
                        arrivals.GetValueOrDefault(item.Id))).ToArray(),
                captured.Scenario.Inputs.StaffShifts?.Shifts);
            var changed = original.Keys.Union(allocations.Keys).Count(staff => original.GetValueOrDefault(staff) != allocations.GetValueOrDefault(staff));
            var cost = planning.Request.Constraints.Checkpoints.Sum(item =>
                (long)Math.Abs(configuration.Single(value => value.CheckpointId == item.CheckpointId).StaffCount - item.CurrentStaff) * item.ChangeCost);
            var metrics = replica.ResultsValid ? Metrics(run, captured, snapshots.Where(item => item.RunId == run.Id).ToArray(),
                traces.Where(item => item.RunId == run.Id).ToArray()) : null;
            StaffingCandidateAssessment assessment;
            if (replica.Status is "pending" or "running") assessment = new("pending", []);
            else if (!replica.ResultsValid) assessment = new("failed", [run.FailureReason ?? replica.Status]);
            else if (metrics is null) assessment = new("unassessable", ["kpi_unavailable"]);
            else if (variant.Position == 0) { baselineMetrics = metrics; assessment = new("baseline", []); }
            else
            {
                var comparable = RunComparisonPolicy.Assess(baseline, run);
                assessment = !comparable.Comparable ? new("rejected", comparable.Issues.Select(item => item.Code).ToArray())
                    : baselineMetrics is null ? new("unassessable", ["baseline_metrics_unavailable"])
                    : StaffingEvaluationPolicy.Assess(baselineMetrics, metrics, cost, request.Policy);
            }
            result.Add(new(variant.Id, variant.ScenarioId, run.Id, variant.Position, replica.Status, configuration, changed, cost, metrics, assessment, schedule));
        }
        var recommended = stored.Status is "completed" or "completed_with_failures" ? StaffingEvaluationPolicy.Recommend(result) : null;
        var details = new StaffingEvaluationDetails(batch, planning, request.Policy, StaffingEvaluationPolicy.Ranking, recommended, result);
        if (request.MultiObjective is not null && manifest.TryGetProperty("paretoPlanning", out var weighted) && weighted.ValueKind == JsonValueKind.Object)
            details = details with { Pareto = ParetoAnalysisPolicy.Analyze(details, ContractJson.Read<ParetoSearchResult>(weighted, "ParetoSearchResult")) };
        return details;
    }

    private static StaffingSimulatedMetrics? Metrics(SimulationRun run, RunInputSnapshot capture, IReadOnlyList<RunSnapshot> stored,
        IReadOnlyList<RunSnapshot> traces)
    {
        var checkpoints = capture.Layout.Layout.Checkpoints.Select(item => item.Id).ToHashSet();
        if (stored.Count != checkpoints.Count || !checkpoints.SetEquals(stored.Select(item => item.ZoneId)) || traces.Count != 1) return null;
        var horizon = capture.Scenario.Inputs.DurationMinutes;
        var recordedAt = capture.Scenario.Inputs.StartsAtUtc.AddMinutes(horizon);
        var rows = new List<KpiSnapshot>();
        ProcessResult process;
        try
        {
            foreach (var item in stored)
            {
                using var json = JsonDocument.Parse(item.PayloadJson);
                var row = ContractJson.Read<KpiSnapshot>(json.RootElement, "KpiSnapshot");
                if (row.RunId != run.Id || row.ZoneId != item.ZoneId || row.Minute != horizon
                    || item.RecordedAtUtc != recordedAt || !KpiContractValidation.Timestamp(row.RecordedAtUtc, out var timestamp)
                    || timestamp != new DateTimeOffset(recordedAt)) return null;
                rows.Add(row);
            }
            var trace = traces[0];
            if (trace.SceneId != run.SceneId || trace.ZoneId != run.SceneId || trace.RecordedAtUtc != recordedAt) return null;
            using var document = JsonDocument.Parse(trace.PayloadJson);
            var payload = document.RootElement;
            if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("kind", out var kind)
                || kind.ValueKind != JsonValueKind.String || SecurityReport(payload, kind.GetString()) is not { } report) return null;
            process = ContractJson.Read<ProcessResult>(report, "ProcessResult");
            if (process.Engine != KpiConventions.Engine || process.HorizonMinutes != horizon || process.Offset != 0
                || process.HasMore || process.ReturnedCount != process.ArrivedCount
                || !checkpoints.SetEquals(process.Checkpoints.Select(item => item.CheckpointId))) return null;
        }
        catch (Exception failure) when (failure is JsonException or ContractValidationException) { return null; }
        var count = rows.Sum(item => item.PopulationCount); var processed = rows.Sum(item => item.ProcessedPassengers);
        var samples = rows.Sum(item => item.WaitSampleCount); var riskPopulation = rows.Sum(item => item.BoardingRiskPopulationCount);
        var risk = rows.Sum(item => item.BoardingAtRiskCount);
        // Only completed services belong to the KPI wait population. Pool the persisted observations,
        // never checkpoint percentiles, to obtain the population's nearest-rank P95.
        var waits = process.Passengers.Where(item => item.SecurityClearedMinute.HasValue).Select(item => item.QueueWaitMinutes!.Value).Order().ToArray();
        if (process.PopulationCount != count || process.ArrivedCount != rows.Sum(item => item.ArrivedPassengers) || waits.Length != samples
            || rows.Any(row => row.ProcessedPassengers != process.Checkpoints.Single(item => item.CheckpointId == row.ZoneId).ProcessedCount)) return null;
        return new(count, processed, count - processed, samples,
            samples == 0 ? null : rows.Sum(item => (item.MeanWaitMinutes ?? 0) * item.WaitSampleCount) / samples,
            samples == 0 ? null : waits[(int)Math.Ceiling(.95 * samples) - 1], riskPopulation == 0 ? null : (double)risk / riskPopulation, risk, riskPopulation,
            rows.OrderBy(item => item.ZoneId).Select(item => new StaffingCheckpointObservation(item.ZoneId, item.PopulationCount,
                item.ProcessedPassengers, item.WaitSampleCount, item.MeanWaitMinutes, item.P95WaitMinutes,
                item.BoardingRiskPopulationCount, item.BoardingAtRiskCount)).ToArray());
    }

    // A journey run's scene snapshot is its journey report, which carries the security simulation it built on.
    private static JsonElement? SecurityReport(JsonElement payload, string? kind) => kind switch
    {
        "processes/v1" when payload.TryGetProperty("processResult", out var direct) => direct,
        "journeys/v1" when payload.TryGetProperty("journeyResult", out var journey) && journey.ValueKind == JsonValueKind.Object
            && journey.TryGetProperty("upstreamResult", out var upstream) && upstream.ValueKind == JsonValueKind.Object
            && upstream.TryGetProperty("processResult", out var nested) => nested,
        _ => null
    };
}
