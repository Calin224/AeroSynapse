using System.Text.Json;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Management;
using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.DigitalTwin;
using Microsoft.EntityFrameworkCore;

namespace AeroSynapse.Infrastructure.Execution;

public sealed partial class BatchService
{
    public async Task<StaffingEvaluationDetails> CreateStaffingEvaluationAsync(Guid scenarioId, CreateStaffingEvaluationRequest request, CancellationToken token)
    {
        Validate(request.CommandId, request.Name, request.TimeLimitSeconds);
        if (request.Proposal is null || request.CandidateCount is < 1 or > 4 || !StaffingEvaluationPolicy.Valid(request.Policy))
            throw Invalid("evaluation", "Supply staffing constraints, 1–4 candidates and finite nonnegative acceptance tolerances.");
        if (request.MultiObjective is not null && (!ParetoModel.Valid(request.MultiObjective) || request.CandidateCount < 2))
            throw Invalid("multiObjective", "Supply nonzero weights, positive normalization references, valid limits and 2–4 candidates.");
        if (request.Shifts is not null && request.MultiObjective is not null)
            throw Invalid("shifts", "Shift plans are compared with the best fixed plan, without a multi-objective search.");
        var identity = JsonSerializer.Serialize(new { kind = "staffing-evaluation/v1", scenarioId, request }, Json);
        await using (var transaction = await db.Database.BeginTransactionAsync(token))
        {
            if (!await Exists(request.CommandId, identity, token))
            {
                await LockScenarios([scenarioId], token);
                var source = await scenarios.GetAsync(scenarioId, token) ?? throw new ScenarioNotFoundException("Scenario was not found.");
                if (source.Scenario.DeletedAtUtc is not null) throw new ScenarioConflictException("Archived scenarios cannot create staffing evaluations.");
                if (source.Scenario.Revision != request.Proposal.Revision) throw new ScenarioConflictException("Scenario has changed. Reload before evaluating staffing.");
                if (source.Inputs.PassengerGeneration?.Flow is not null)
                    throw Invalid("passengerGeneration.flow", "Staffing evaluation requires the checkpoint KPI engine; alternative flows do not provide these KPI snapshots.");
                source = source with { Inputs = FlightScheduling.Resolve(source.Inputs) };
                var layout = await scenarios.GetLayoutAsync(source.Scenario.SceneId, source.Scenario.LayoutVersionId, token)
                    ?? throw new ScenarioNotFoundException("Captured layout was not found.");
                var model = await sender.Send(new BuildStaffingModelQuery(scenarioId, request.Proposal), token);
                // Shift mode keeps one fixed candidate: the best plan that holds all day, to show what shifts add.
                var search = new StaffingCandidateSearchRequest(model, request.Shifts is null ? request.CandidateCount : 1);
                ParetoSearchResult? paretoPlanning = null;
                StaffingCandidateSearchResult planning;
                if (request.MultiObjective is { } options)
                {
                    var weighted = ContractJson.Read<ParetoSearchRequest>(JsonSerializer.SerializeToElement(
                        new ParetoSearchRequest(model, request.CandidateCount, source.Inputs.DurationMinutes, options), ContractJson.Options), "ParetoSearchRequest");
                    paretoPlanning = await staffingPlanner.GenerateParetoAsync(weighted, token);
                    if (!ParetoModel.ValidResult(weighted, paretoPlanning)) throw new StaffingPlannerUnavailableException();
                    planning = new(search, paretoPlanning.Status, paretoPlanning.Candidates, paretoPlanning.WallTimeSeconds);
                }
                else planning = await staffingPlanner.GenerateCandidatesAsync(search, token);
                if (!StaffingValidation.ValidCandidates(search, planning)) throw new StaffingPlannerUnavailableException();
                var shiftPlanning = new List<ShiftPlanResult>();
                if (request.Shifts is { } rules)
                    // The rules as set, then the same day without moves between checkpoints.
                    foreach (var variant in new[] { rules, rules with { MaxMovesPerHour = 0 } }.Distinct())
                    {
                        var shiftRequest = await sender.Send(new BuildShiftPlanQuery(scenarioId, request.Proposal, variant, ShiftSolveSeconds), token);
                        var plan = await staffingPlanner.PlanShiftsAsync(shiftRequest, token);
                        if (plan.Shifts.Count != 0) shiftPlanning.Add(plan);
                    }
                if (request.Shifts is not null && shiftPlanning.Count == 0)
                    throw Invalid("shifts", "No shift plan meets these rules within the solver time. Relax the shift rules and try again.");
                var now = DateTime.UtcNow;
                RunInputSnapshot Capture(ScenarioDetails value) => new(1, now, value, layout, KpiConventions.Engine, value.Inputs.SnapshotIntervalMinutes ?? 1);
                var captures = new List<RunInputSnapshot> { Capture(source) };
                var name = request.Name.Trim(); if (name.Length > 130) name = name[..(char.IsHighSurrogate(name[129]) ? 129 : 130)];
                // A fixed candidate drops any shift plan, so its allocation is what staffs the day.
                var candidates = planning.Candidates.Select(candidate => (Label: "candidat", Inputs: source.Inputs with
                    { StaffAllocations = StaffShiftScenarios.Allocate(source.Inputs, layout.Layout,
                        candidate.Assignments.ToDictionary(item => item.CheckpointId, item => item.StaffCount), "candidates"), StaffShifts = null }))
                    .Concat(shiftPlanning.Select(plan => (Label: "ture", Inputs: source.Inputs with { StaffShifts = Shifts(plan, source.Inputs.DurationMinutes) })))
                    .DistinctBy(item => JsonSerializer.Serialize(item.Inputs, Json)).ToArray();
                foreach (var ((label, inputs), index) in candidates.Select((item, index) => (item, index)))
                {
                    var copy = await scenarios.CreateAsync(new($"STAFF-{request.CommandId:N}-{index + 1}", $"{name} · {label} {index + 1}",
                        source.Scenario.Description, source.Scenario.SceneId, source.Scenario.LayoutVersionId, inputs), token);
                    copy = await CarryDataSource(source, copy, token);
                    if (!JsonElement.DeepEquals(JsonSerializer.SerializeToElement(StaffingOnly(source.Inputs), Json),
                        JsonSerializer.SerializeToElement(StaffingOnly(copy.Inputs), Json)))
                        throw Invalid("candidates", "A staffing candidate must preserve every population, seed, event and duration input.");
                    captures.Add(Capture(copy));
                }
                var manifest = JsonSerializer.Serialize(new { kind = "staffing-evaluation/v1",
                    command = JsonSerializer.Deserialize<JsonElement>(identity), planning, paretoPlanning,
                    shiftPlanning = shiftPlanning.Count == 0 ? null : shiftPlanning }, Json);
                Add(request.CommandId, request.Name, request.TimeLimitSeconds, [source.Inputs.Seed], captures, manifest, null, now);
                await db.SaveChangesAsync(token);
            }
            await transaction.CommitAsync(token);
        }
        return await GetStaffingEvaluationAsync(request.CommandId, token);
    }

    private const double ShiftSolveSeconds = 12;

    /// <summary>The inputs a staffing change may not touch: everything except the fixed allocation and the shift plan.</summary>
    public static ScenarioInputs StaffingOnly(ScenarioInputs inputs) => inputs with { StaffAllocations = [], StaffShifts = null };

    private static ScenarioStaffShifts Shifts(ShiftPlanResult plan, int durationMinutes) => new(plan.Request.SlotMinutes,
        plan.Request.Rules.TransferMinutes, plan.Shifts.Select(shift => new ScenarioStaffShift(shift.StaffMemberId,
            shift.Segments.Select(segment => new ScenarioShiftSegment(segment.CheckpointId, segment.StartSlot * plan.Request.SlotMinutes,
                Math.Min(segment.EndSlot * plan.Request.SlotMinutes, durationMinutes))).ToArray())).ToArray());

    public Task<StaffingEvaluationDetails> GetStaffingEvaluationAsync(Guid batchId, CancellationToken token) =>
        new StaffingEvaluationReader(db).GetAsync(batchId, token);

    public async Task<IReadOnlyList<BatchSummary>> ListStaffingEvaluationsAsync(Guid scenarioId, CancellationToken token)
    {
        var rows = await db.Batches.AsNoTracking().Where(item => EF.Functions.JsonContains(item.RequestJson, "{\"kind\":\"staffing-evaluation/v1\"}")
            && db.BatchVariants.Any(variant => variant.BatchId == item.Id && variant.Position == 0 && variant.ScenarioId == scenarioId))
            .OrderByDescending(item => item.CreatedAtUtc).ThenByDescending(item => item.Id).Take(50).ToArrayAsync(token);
        return rows.Select(Summary).ToArray();
    }
}
