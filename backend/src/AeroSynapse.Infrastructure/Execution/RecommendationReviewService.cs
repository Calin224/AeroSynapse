using System.Data;
using System.Text.Json;
using AeroSynapse.Application.Management;
using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.DigitalTwin;
using AeroSynapse.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AeroSynapse.Infrastructure.Execution;

public sealed class RecommendationReviewService(TwinDbContext db, IScenarioService scenarios) : IRecommendationReviewService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<RecommendationOutcome> GetForEvaluationAsync(Guid evaluationId, CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        return await Outcome(evaluationId, token);
    }

    public async Task<RecommendationOutcome> CreateAsync(Guid evaluationId, CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        // One retained recommendation per evaluation, including concurrent requests.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM twin.simulation_batches WHERE \"Id\" = {evaluationId} FOR UPDATE", token);
        var existing = await db.RecommendationReviews.AsNoTracking().SingleOrDefaultAsync(item => item.EvaluationId == evaluationId, token);
        if (existing is not null)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM twin.recommendation_reviews WHERE \"Id\" = {existing.Id} FOR UPDATE", token);
            return new("available", "Recomandare salvată.", await Details(await RequireReview(existing.Id, token), token));
        }
        var evaluation = await new StaffingEvaluationReader(db).GetAsync(evaluationId, token);
        var outcome = Unprepared(evaluation);
        if (outcome.Status != "not_created") return outcome;
        var baseline = evaluation.Variants.Single(item => item.Position == 0);
        var candidate = evaluation.Variants.Single(item => item.RunId == evaluation.RecommendedRunId);
        var beforeRun = await db.Runs.AsNoTracking().SingleAsync(item => item.Id == baseline.RunId, token);
        var afterRun = await db.Runs.AsNoTracking().SingleAsync(item => item.Id == candidate.RunId, token);
        var before = Input(beforeRun); var after = Input(afterRun);
        if (!beforeRun.IsSynthetic || !afterRun.IsSynthetic || !before.Scenario.Scenario.IsSynthetic
            || !after.Scenario.Scenario.IsSynthetic || !before.Layout.Layout.Scene.IsSynthetic)
            throw new ScenarioConflictException("Only synthetic demonstrations support recommendation approval.");
        if (!JsonElement.DeepEquals(JsonSerializer.SerializeToElement(BatchService.StaffingOnly(before.Scenario.Inputs), Json),
                JsonSerializer.SerializeToElement(BatchService.StaffingOnly(after.Scenario.Inputs), Json)))
            throw new ScenarioConflictException("The recommendation must change staffing only.");
        var explanation = Explain(evaluation, baseline, candidate, before, after);
        var now = DateTime.UtcNow;
        var recommendation = new RunRecommendation { Id = Guid.NewGuid(), SceneId = beforeRun.SceneId,
            BaselineRunId = baseline.RunId, IsSynthetic = true, CreatedAtUtc = now,
            Objective = "Redistribuire personal validată în simulator", BaselineScore = 0,
            Rationale = explanation.Benefit };
        var review = new RecommendationReview { Id = recommendation.Id, SceneId = recommendation.SceneId, IsSynthetic = true,
            EvaluationId = evaluationId, ScenarioId = baseline.ScenarioId, ScenarioRevision = before.Scenario.Scenario.Revision,
            CandidateRunId = candidate.RunId, ExplanationJson = JsonSerializer.Serialize(explanation, Json) };
        db.Recommendations.Add(recommendation);
        // Keep provenance and the existing heatmap comparison query usable.
        db.RecommendationCandidates.Add(new() { Id = Guid.NewGuid(), SceneId = recommendation.SceneId, IsSynthetic = true,
            RecommendationId = recommendation.Id, RunId = candidate.RunId, Rank = 1, Score = 0,
            AssessmentJson = JsonSerializer.Serialize(candidate.Assessment, Json) });
        db.RecommendationReviews.Add(review);
        db.RecommendationAudit.Add(new() { Id = Guid.NewGuid(), SceneId = review.SceneId, IsSynthetic = true,
            RecommendationId = review.Id, CommandId = Guid.NewGuid(), Action = "created", Actor = "system",
            RecordedAtUtc = now, ScenarioRevisionBefore = review.ScenarioRevision, ScenarioRevisionAfter = review.ScenarioRevision,
            BeforeJson = JsonSerializer.Serialize(before.Scenario.Inputs, Json), AfterJson = JsonSerializer.Serialize(before.Scenario.Inputs, Json) });
        await db.SaveChangesAsync(token);
        var details = await Details(review, token);
        await transaction.CommitAsync(token);
        return new("available", "Recomandare pregătită pentru decizia operatorului.", details);
    }

    public async Task<RecommendationDecisionResult> DecideAsync(Guid id, string action, RecommendationDecisionRequest request, CancellationToken token)
    {
        if (action is not ("approved" or "rejected")) throw Invalid("action", "Use approve or reject.");
        if (request.CommandId == Guid.Empty) throw Invalid("commandId", "A nonempty command ID is required.");
        if (string.IsNullOrWhiteSpace(request.Actor) || request.Actor.Length > 160 || request.Actor.Contains('\0'))
            throw Invalid("actor", "Supply an operator name of 1–160 characters.");
        if (request.Reason is null || request.Reason.Length > 2000 || request.Reason.Contains('\0'))
            throw Invalid("reason", "Reason must contain at most 2000 characters without NUL.");
        var actor = request.Actor.Trim(); var reason = request.Reason.Trim();
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var review = await RequireReview(id, token);
        // Scenario-first order also serializes different recommendations against the
        // same revision, and agrees with scenario retention and editing.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM twin.scenarios WHERE \"Id\" = {review.ScenarioId} FOR UPDATE", token);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM twin.recommendation_reviews WHERE \"Id\" = {id} FOR UPDATE", token);
        review = await RequireReview(id, token);
        var command = await db.RecommendationAudit.AsNoTracking().SingleOrDefaultAsync(item => item.CommandId == request.CommandId, token);
        if (command is not null && (command.RecommendationId != id || command.Action != action || command.Actor != actor || command.Reason != reason))
            throw new ScenarioConflictException("The command ID was already used for a different decision.");
        if (review.Status != "pending")
        {
            if (review.Status != action) throw new ScenarioConflictException("This recommendation already has a final decision.");
            return new(await Details(review, token), action == "approved" ? await scenarios.GetAsync(review.ScenarioId, token) : null);
        }
        var current = await scenarios.GetAsync(review.ScenarioId, token)
            ?? throw new ScenarioNotFoundException("Scenario was not found.");
        var beforeJson = JsonSerializer.Serialize(current.Inputs, Json);
        ScenarioDetails? updated = null;
        if (action == "approved")
        {
            if (!review.IsSynthetic || !current.Scenario.IsSynthetic || current.Scenario.DeletedAtUtc is not null)
                throw new ScenarioConflictException("Approval requires an active synthetic scenario.");
            if (current.Scenario.Revision != review.ScenarioRevision)
                throw new ScenarioConflictException("Scenariul s-a modificat de la evaluare. Rulează o evaluare nouă înainte de aprobare.");
            var parent = await db.Recommendations.AsNoTracking().SingleAsync(item => item.Id == id, token);
            var baselineRun = await db.Runs.AsNoTracking().SingleAsync(item => item.Id == parent.BaselineRunId, token);
            var candidateRun = await db.Runs.AsNoTracking().SingleAsync(item => item.Id == review.CandidateRunId, token);
            if (!baselineRun.ResultsValid || !candidateRun.ResultsValid || !baselineRun.IsSynthetic || !candidateRun.IsSynthetic)
                throw new ScenarioConflictException("Approval requires successful synthetic runs.");
            var baseline = Input(baselineRun); var candidate = Input(candidateRun);
            if (current.Scenario.LayoutVersionId != baseline.Layout.Version.Id
                || !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(FlightScheduling.Resolve(current.Inputs), Json),
                    JsonSerializer.SerializeToElement(baseline.Scenario.Inputs, Json)))
                throw new ScenarioConflictException("Scenario inputs no longer match the evaluated baseline.");
            // Update only the stored demonstration aggregate. There is deliberately
            // no engine dispatch, equipment adapter or outbound command here.
            updated = await scenarios.UpdateAsync(review.ScenarioId, new(current.Scenario.Code, current.Scenario.Name,
                current.Scenario.Description, current.Scenario.SceneId, current.Scenario.LayoutVersionId,
                current.Inputs with { StaffAllocations = candidate.Scenario.Inputs.StaffAllocations, StaffShifts = candidate.Scenario.Inputs.StaffShifts },
                current.Scenario.Revision), token);
        }
        await db.RecommendationReviews.Where(item => item.Id == id).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, action), token);
        db.RecommendationAudit.Add(new() { Id = Guid.NewGuid(), SceneId = review.SceneId, IsSynthetic = true,
            RecommendationId = id, CommandId = request.CommandId, Action = action, Actor = actor, Reason = reason,
            RecordedAtUtc = DateTime.UtcNow, ScenarioRevisionBefore = current.Scenario.Revision,
            ScenarioRevisionAfter = updated?.Scenario.Revision ?? current.Scenario.Revision,
            BeforeJson = beforeJson, AfterJson = updated is null ? beforeJson : JsonSerializer.Serialize(updated.Inputs, Json) });
        try { await db.SaveChangesAsync(token); }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        { throw new ScenarioConflictException("The command ID was already used for another decision."); }
        await transaction.CommitAsync(token);
        review.Status = action;
        return new(await Details(review, token), updated);
    }

    private async Task<RecommendationOutcome> Outcome(Guid evaluationId, CancellationToken token)
    {
        var review = await db.RecommendationReviews.AsNoTracking().SingleOrDefaultAsync(item => item.EvaluationId == evaluationId, token);
        if (review is not null) return new("available", "Recomandare salvată.", await Details(review, token));
        var evaluation = await new StaffingEvaluationReader(db).GetAsync(evaluationId, token);
        return Unprepared(evaluation);
    }

    private static RecommendationOutcome Unprepared(StaffingEvaluationDetails evaluation)
    {
        if (evaluation.Batch.Batch.Status is "pending" or "running")
            return new("evaluation_incomplete", "Evaluarea este în desfășurare; aprobarea nu este disponibilă.", null);
        if (evaluation.RecommendedRunId is null)
            return new("no_superior_candidate", "Nu există candidat superior validat față de baseline. Nu se propune nicio modificare.", null);
        return new("not_created", "Candidatul superior poate fi transformat într-o recomandare verificabilă.", null);
    }

    private async Task<RecommendationReview> RequireReview(Guid id, CancellationToken token) =>
        await db.RecommendationReviews.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, token)
            ?? throw new ScenarioNotFoundException("Recommendation review was not found.");

    private async Task<RecommendationReviewDetails> Details(RecommendationReview review, CancellationToken token)
    {
        var parent = await db.Recommendations.AsNoTracking().SingleAsync(item => item.Id == review.Id, token);
        var audit = await db.RecommendationAudit.AsNoTracking().Where(item => item.RecommendationId == review.Id)
            .OrderBy(item => item.RecordedAtUtc).ThenBy(item => item.Id).ToArrayAsync(token);
        return new(review.Id, review.EvaluationId, review.ScenarioId, review.ScenarioRevision, parent.BaselineRunId,
            review.CandidateRunId, review.Status, parent.CreatedAtUtc,
            JsonSerializer.Deserialize<RecommendationExplanation>(review.ExplanationJson, Json)!, audit);
    }

    private static RecommendationExplanation Explain(StaffingEvaluationDetails evaluation, StaffingEvaluatedVariant baseline,
        StaffingEvaluatedVariant candidate, RunInputSnapshot before, RunInputSnapshot after)
    {
        var original = before.Scenario.Inputs.StaffAllocations.ToDictionary(item => item.StaffMemberId, item => (Guid?)item.CheckpointId);
        var proposed = after.Scenario.Inputs.StaffAllocations.ToDictionary(item => item.StaffMemberId, item => (Guid?)item.CheckpointId);
        string Name(Guid? id) => id is null ? "Nealocat" : before.Layout.Layout.Checkpoints.Single(item => item.Id == id).Name;
        var moves = original.Keys.Union(proposed.Keys).Order().Where(id => original.GetValueOrDefault(id) != proposed.GetValueOrDefault(id))
            .Select(id => new RecommendationResourceMove(id, before.Layout.Layout.Staff.Single(item => item.Id == id).DisplayName,
                original.GetValueOrDefault(id), Name(original.GetValueOrDefault(id)), proposed.GetValueOrDefault(id), Name(proposed.GetValueOrDefault(id)))).ToArray();
        var checkpoints = baseline.Configuration.Select(item => new RecommendationCheckpointChange(item.CheckpointId, item.Name,
            item.StaffCount, candidate.Configuration.Single(other => other.CheckpointId == item.CheckpointId).StaffCount)).ToArray();
        var estimates = new List<RecommendationEstimate>();
        void Add(string metric, string unit, double? first, double? second)
        { if (first.HasValue && second.HasValue) estimates.Add(new(metric, unit, first.Value, second.Value, second.Value - first.Value)); }
        var b = baseline.Metrics!; var c = candidate.Metrics!;
        Add("Așteptare medie", "min", b.MeanWaitMinutes, c.MeanWaitMinutes);
        Add("P95 așteptare", "min", b.P95WaitMinutes, c.P95WaitMinutes);
        Add("Risc boarding", "%", b.BoardingRisk * 100, c.BoardingRisk * 100);
        Add("Neprocesați", "pasageri", b.UnprocessedCount, c.UnprocessedCount);
        foreach (var first in b.Checkpoints)
        {
            var second = c.Checkpoints.Single(item => item.CheckpointId == first.CheckpointId);
            Add(Name(first.CheckpointId) + " · așteptare medie", "min", first.MeanWaitMinutes, second.MeanWaitMinutes);
            Add(Name(first.CheckpointId) + " · P95", "min", first.P95WaitMinutes, second.P95WaitMinutes);
            Add(Name(first.CheckpointId) + " · neprocesați", "pasageri", first.PopulationCount - first.ProcessedCount, second.PopulationCount - second.ProcessedCount);
            if (first.BoardingRiskPopulationCount > 0 && second.BoardingRiskPopulationCount > 0)
                Add(Name(first.CheckpointId) + " · risc boarding", "%", 100d * first.BoardingAtRiskCount / first.BoardingRiskPopulationCount,
                    100d * second.BoardingAtRiskCount / second.BoardingRiskPopulationCount);
        }
        var shifts = after.Scenario.Inputs.StaffShifts;
        var risks = new List<string> { "Estimări sintetice dintr-o singură pereche de rulări; nu garantează performanța reală.",
            shifts is null ? "Realocarea presupune disponibilitatea personalului și nu modelează timpul de deplasare sau schimbarea turelor."
                : $"Planul pe ture presupune că fiecare persoană e disponibilă în tura propusă; o mutare între controale costă {shifts.TransferMinutes} min de serviciu." };
        risks.AddRange(estimates.Where(item => item.Delta > 1e-9).Select(item => $"Compromis: {item.Metric} crește de la {item.Before:G6} la {item.After:G6} {item.Unit}, în toleranța capturată."));
        if (evaluation.Planning.Status != "complete") risks.Add("Căutarea nu a produs toți candidații solicitați; recomandarea se limitează la candidații evaluați.");
        if (evaluation.Batch.Batch.Status == "completed_with_failures") risks.Add("Unele rulări au eșuat; sunt comparate doar rezultatele valide.");
        var benefit = c.BoardingAtRiskCount < b.BoardingAtRiskCount
            ? $"Pasageri cu risc de boarding: {b.BoardingAtRiskCount} → {c.BoardingAtRiskCount}."
            : c.UnprocessedCount < b.UnprocessedCount ? $"Pasageri neprocesați: {b.UnprocessedCount} → {c.UnprocessedCount}."
            : c.MeanWaitMinutes < b.MeanWaitMinutes ? $"Așteptare medie estimată: {b.MeanWaitMinutes:G6} → {c.MeanWaitMinutes:G6} min."
            : $"P95 estimat: {b.P95WaitMinutes:G6} → {c.P95WaitMinutes:G6} min.";
        return new(moves, checkpoints, estimates,
            [$"Aceeași populație ({b.PopulationCount}), seed {before.Scenario.Inputs.Seed}, evenimente, layout și durată ({before.Scenario.Inputs.DurationMinutes} min).",
             "Waiting și P95 folosesc serviciile terminate; riscul de boarding folosește pasagerii cu termen observabil.",
             $"Observații waiting: baseline {b.WaitSampleCount}, candidat {c.WaitSampleCount}; boarding la risc: baseline {b.BoardingAtRiskCount}/{b.BoardingRiskPopulationCount}, candidat {c.BoardingAtRiskCount}/{c.BoardingRiskPopulationCount}.",
             "Restricțiile și toleranțele sunt cele capturate la evaluare. Aprobarea cere aceeași revizie a scenariului.",
             shifts is null ? "Aprobarea modifică exclusiv alocările scenariului DEMO; nu transmite comenzi către echipamente reale."
                : "Aprobarea scrie programul pe ture în scenariul DEMO; nu transmite comenzi către echipamente reale."],
            evaluation.Planning.Request.Constraints, evaluation.Policy,
            benefit + " Candidatul validat depășește baseline-ul în ordinea: risc boarding, neprocesați, așteptare medie, P95, cost. Verifică estimările înainte/după și compromisurile.",
            risks, evaluation.Ranking, moves.Length, candidate.ReallocationCost,
            // Only a recommended shift plan changes when people work; a fixed winner keeps the moves list.
            baseline.Schedule is null || candidate.Schedule?.Shifts is null ? null : baseline.Schedule.Checkpoints.Select(row => new RecommendationScheduleChange(
                row.CheckpointId, row.Name, row.Staff, candidate.Schedule.Checkpoints.Single(other => other.CheckpointId == row.CheckpointId).Staff)).ToArray());
    }

    private static RunInputSnapshot Input(SimulationRun run) => JsonSerializer.Deserialize<RunInputSnapshot>(run.InputSnapshotJson!, Json)!;
    private static ScenarioValidationException Invalid(string field, string message) => new(new Dictionary<string, string[]> { [field] = [message] });
}
