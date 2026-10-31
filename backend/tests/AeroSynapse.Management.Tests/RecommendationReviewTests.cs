using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroSynapse.Application.Management;
using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.DigitalTwin;
using AeroSynapse.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AeroSynapse.Management.Tests;

public sealed class RecommendationReviewTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static string Path(Guid evaluationId) => $"/staffing-evaluations/{evaluationId}/recommendation";
    private static async Task<RecommendationOutcome> Prepare(ManagementHost host, Guid evaluationId)
    {
        using var response = await host.Client.PostAsync(Path(evaluationId), null, Token);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Token));
        return (await response.Content.ReadFromJsonAsync<RecommendationOutcome>(Json, Token))!;
    }
    private static async Task<(ScenarioDetails Source, StaffingEvaluationDetails Evaluation)> Evaluate(ManagementHost host)
    {
        var source = await StaffingEvaluationTests.Create(host);
        var request = await StaffingEvaluationTests.Request(host, source);
        await StaffingEvaluationTests.Start(host, source.Scenario.Id, request);
        await StaffingEvaluationTests.Drain(host);
        var evaluation = (await host.Client.GetFromJsonAsync<StaffingEvaluationDetails>($"/staffing-evaluations/{request.CommandId}", Json, Token))!;
        Assert.NotNull(evaluation.RecommendedRunId);
        return (source, evaluation);
    }
    private static Task<HttpResponseMessage> Decide(ManagementHost host, Guid id, string action, RecommendationDecisionRequest request) =>
        host.Client.PostAsJsonAsync($"/recommendation-reviews/{id}/{action}", request, Json, Token);

    [Theory]
    [InlineData("approve", "approved")]
    [InlineData("reject", "rejected")]
    public async Task Explainable_recommendations_and_concurrent_decisions_are_persistent_atomic_and_idempotent(string action, string status)
    {
        await using var host = await ManagementHost.CreateAsync(realEngine: true);
        var (source, evaluation) = await Evaluate(host);
        var notCreated = (await host.Client.GetFromJsonAsync<RecommendationOutcome>(Path(evaluation.Batch.Batch.Id), Json, Token))!;
        Assert.Equal("not_created", notCreated.Status); Assert.Null(notCreated.Recommendation);
        var prepared = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Prepare(host, evaluation.Batch.Batch.Id)));
        var review = prepared[0].Recommendation!;
        Assert.All(prepared, value => Assert.Equal(review.Id, value.Recommendation!.Id));
        Assert.Equal(evaluation.Variants[0].RunId, review.BaselineRunId);
        Assert.Equal(evaluation.RecommendedRunId, review.CandidateRunId);
        Assert.NotEqual(Guid.Empty, review.CandidateRunId); Assert.NotEqual(review.BaselineRunId, review.CandidateRunId);
        Assert.NotEmpty(review.Explanation.Resources); Assert.NotEmpty(review.Explanation.Estimates);
        Assert.All(review.Explanation.Resources, move => Assert.NotEqual(move.BeforeCheckpointId, move.AfterCheckpointId));
        Assert.All(review.Explanation.Estimates, estimate => Assert.Equal(estimate.After - estimate.Before, estimate.Delta));
        var winner = evaluation.Variants.Single(item => item.RunId == review.CandidateRunId);
        var mean = review.Explanation.Estimates.Single(item => item.Metric == "Așteptare medie");
        Assert.Equal(evaluation.Variants[0].Metrics!.MeanWaitMinutes, mean.Before);
        Assert.Equal(winner.Metrics!.MeanWaitMinutes, mean.After);
        Assert.Equal(winner.ReallocatedPeople, review.Explanation.Resources.Count);
        Assert.All(review.Explanation.Checkpoints, checkpoint => {
            Assert.Equal(evaluation.Variants[0].Configuration.Single(item => item.CheckpointId == checkpoint.CheckpointId).StaffCount, checkpoint.Before);
            Assert.Equal(winner.Configuration.Single(item => item.CheckpointId == checkpoint.CheckpointId).StaffCount, checkpoint.After);
        });
        Assert.NotEmpty(review.Explanation.Assumptions); Assert.NotEmpty(review.Explanation.Risks);
        Assert.Equal(evaluation.Policy, review.Explanation.AcceptancePolicy);
        Assert.Single(review.Audit); Assert.Equal("created", review.Audit[0].Action);
        var original = (await host.Client.GetFromJsonAsync<ScenarioDetails>($"/scenarios/{source.Scenario.Id}", Json, Token))!;
        Assert.Equal(JsonSerializer.Serialize(source, Json), JsonSerializer.Serialize(original, Json));
        await using var scope = host.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<TwinDbContext>();
        var liveStaff = JsonSerializer.Serialize(await db.Staff.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync(Token), Json);
        var runs = await db.Runs.CountAsync(Token); var commands = await db.SimulationCommands.CountAsync(Token);
        var decision = new RecommendationDecisionRequest(Guid.NewGuid(), "Operator DEMO", "Verificat în simulator");
        var responses = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Decide(host, review.Id, action, decision)));
        foreach (var response in responses)
        {
            using (response) Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Token));
        }
        using var retry = await Decide(host, review.Id, action, decision with { CommandId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var final = (await host.Client.GetFromJsonAsync<RecommendationOutcome>(Path(evaluation.Batch.Batch.Id), Json, Token))!.Recommendation!;
        Assert.Equal(status, final.Status); Assert.Equal(2, final.Audit.Count);
        var audit = final.Audit.Single(item => item.Action == status);
        Assert.Equal(decision.CommandId, audit.CommandId); Assert.Equal("test-admin", audit.Actor); Assert.Equal(decision.Reason, audit.Reason);
        Assert.Equal(JsonSerializer.Serialize(review.Explanation, Json), JsonSerializer.Serialize(final.Explanation, Json));
        var current = (await host.Client.GetFromJsonAsync<ScenarioDetails>($"/scenarios/{source.Scenario.Id}", Json, Token))!;
        if (action == "approve")
        {
            Assert.Equal(source.Scenario.Revision + 1, current.Scenario.Revision);
            var candidate = await db.Runs.AsNoTracking().SingleAsync(item => item.Id == review.CandidateRunId, Token);
            var captured = JsonSerializer.Deserialize<RunInputSnapshot>(candidate.InputSnapshotJson!, Json)!;
            Assert.Equal(captured.Scenario.Inputs.StaffAllocations, current.Inputs.StaffAllocations);
            Assert.Equal(JsonSerializer.Serialize(source.Inputs with { StaffAllocations = [] }, Json), JsonSerializer.Serialize(current.Inputs with { StaffAllocations = [] }, Json));
            Assert.Equal(source.Scenario.Revision + 1, audit.ScenarioRevisionAfter);
        }
        else
        {
            Assert.Equal(JsonSerializer.Serialize(source, Json), JsonSerializer.Serialize(current, Json));
            Assert.Equal(audit.BeforeJson, audit.AfterJson);
        }
        Assert.Equal(liveStaff, JsonSerializer.Serialize(await db.Staff.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync(Token), Json));
        Assert.Equal(runs, await db.Runs.CountAsync(Token)); Assert.Equal(commands, await db.SimulationCommands.CountAsync(Token));
        Assert.Single(await db.RecommendationReviews.ToArrayAsync(Token));
        using var opposite = await Decide(host, review.Id, action == "approve" ? "reject" : "approve", decision with { CommandId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Conflict, opposite.StatusCode);
        using var changedCommand = await Decide(host, review.Id, action, decision with { Reason = "changed" });
        Assert.Equal(HttpStatusCode.Conflict, changedCommand.StatusCode);
        var historical = (await host.Client.GetFromJsonAsync<RecommendationDetails[]>($"/runs/{review.BaselineRunId}/recommendations", Json, Token))!;
        Assert.Equal(review.Id, Assert.Single(historical).Recommendation.Id);
        Assert.True(await db.AuditEvents.AnyAsync(row => row.RecommendationId == review.Id && row.ResourceType == "recommendation_audit", Token));

    }

    [Fact]
    public async Task Stale_approval_leaves_no_mutation_or_decision_audit_but_rejection_remains_available()
    {
        await using var host = await ManagementHost.CreateAsync(realEngine: true);
        var (source, evaluation) = await Evaluate(host);
        var review = (await Prepare(host, evaluation.Batch.Batch.Id)).Recommendation!;
        using var edited = await host.Client.PutAsJsonAsync($"/scenarios/{source.Scenario.Id}", new ScenarioWriteRequest(source.Scenario.Code,
            source.Scenario.Name, source.Scenario.Description, source.Scenario.SceneId, source.Scenario.LayoutVersionId,
            source.Inputs with { Seed = 99 }, source.Scenario.Revision), Json, Token);
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var decision = new RecommendationDecisionRequest(Guid.NewGuid(), "Operator");
        using var approval = await Decide(host, review.Id, "approve", decision);
        Assert.Equal(HttpStatusCode.Conflict, approval.StatusCode);
        var saved = (await host.Client.GetFromJsonAsync<RecommendationOutcome>(Path(evaluation.Batch.Batch.Id), Json, Token))!.Recommendation!;
        Assert.Equal("pending", saved.Status); Assert.Single(saved.Audit);
        using var rejected = await Decide(host, review.Id, "reject", decision);
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
        var current = (await host.Client.GetFromJsonAsync<ScenarioDetails>($"/scenarios/{source.Scenario.Id}", Json, Token))!;
        Assert.Equal(99, current.Inputs.Seed); Assert.Equal(source.Scenario.Revision + 1, current.Scenario.Revision);
    }

    [Fact]
    public async Task Incomplete_and_no_superior_evaluations_never_create_actionable_recommendations()
    {
        await using var host = await ManagementHost.CreateAsync(); var source = await StaffingEvaluationTests.Create(host);
        var request = await StaffingEvaluationTests.Request(host, source);
        request = request with { Proposal = request.Proposal with { AvailableStaff = 0 } };
        await StaffingEvaluationTests.Start(host, source.Scenario.Id, request);
        var pending = await Prepare(host, request.CommandId);
        Assert.Equal("evaluation_incomplete", pending.Status); Assert.Null(pending.Recommendation);
        await StaffingEvaluationTests.Drain(host);
        var none = await Prepare(host, request.CommandId);
        Assert.Equal("no_superior_candidate", none.Status); Assert.Null(none.Recommendation);
        await using var scope = host.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<TwinDbContext>();
        Assert.Empty(await db.Recommendations.ToArrayAsync(Token)); Assert.Empty(await db.RecommendationReviews.ToArrayAsync(Token));
        Assert.Empty(await db.RecommendationAudit.ToArrayAsync(Token));
        using var missing = await host.Client.PostAsync(Path(Guid.NewGuid()), null, Token);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Review_database_constraints_require_existing_baseline_and_candidate_runs()
    {
        await using var host = await ManagementHost.CreateAsync(realEngine: true);
        var (_, evaluation) = await Evaluate(host); var review = (await Prepare(host, evaluation.Batch.Batch.Id)).Recommendation!;
        await using var scope = host.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<TwinDbContext>();
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.RecommendationReviews.Where(item => item.Id == review.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.CandidateRunId, Guid.NewGuid()), Token));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Recommendations.Where(item => item.Id == review.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.BaselineRunId, Guid.NewGuid()), Token));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Runs.Where(item => item.Id == review.CandidateRunId).ExecuteDeleteAsync(Token));
    }

    [Fact]
    public async Task Invalid_decisions_and_non_synthetic_or_archived_scenarios_cannot_be_approved()
    {
        await using var host = await ManagementHost.CreateAsync(realEngine: true);
        var (source, evaluation) = await Evaluate(host); var review = (await Prepare(host, evaluation.Batch.Batch.Id)).Recommendation!;
        var decision = new RecommendationDecisionRequest(Guid.NewGuid(), "Operator");
        foreach (var invalid in new[] { decision with { CommandId = Guid.Empty }, decision with { Actor = " " },
            decision with { Actor = new string('x', 161) }, decision with { Reason = new string('x', 2001) } })
        {
            using var response = await Decide(host, review.Id, "approve", invalid);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        await using var scope = host.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<TwinDbContext>();
        await db.Scenarios.Where(item => item.Id == source.Scenario.Id).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsSynthetic, false), Token);
        using var nonDemo = await Decide(host, review.Id, "approve", decision);
        Assert.Equal(HttpStatusCode.Conflict, nonDemo.StatusCode);
        await db.Scenarios.Where(item => item.Id == source.Scenario.Id).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsSynthetic, true), Token);
        using var archive = await host.Client.DeleteAsync($"/scenarios/{source.Scenario.Id}?retention=archive&revision={source.Scenario.Revision}", Token);
        Assert.Equal(HttpStatusCode.OK, archive.StatusCode);
        using var archived = await Decide(host, review.Id, "approve", decision);
        Assert.Equal(HttpStatusCode.Conflict, archived.StatusCode);
        var saved = (await host.Client.GetFromJsonAsync<RecommendationOutcome>(Path(evaluation.Batch.Batch.Id), Json, Token))!.Recommendation!;
        Assert.Equal("pending", saved.Status); Assert.Single(saved.Audit);
        using var reject = await Decide(host, review.Id, "reject", decision);
        Assert.Equal(HttpStatusCode.OK, reject.StatusCode);
        var current = await db.Scenarios.AsNoTracking().SingleAsync(item => item.Id == source.Scenario.Id, Token);
        Assert.NotNull(current.DeletedAtUtc); Assert.Equal(source.Scenario.Revision + 1, current.Revision);
    }
}
