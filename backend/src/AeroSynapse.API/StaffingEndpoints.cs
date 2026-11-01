using System.Text.Json;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Management;
using MediatR;

namespace AeroSynapse.API;

internal static class StaffingEndpoints
{
    private static RecommendationDecisionRequest AuthenticatedDecision(RecommendationDecisionRequest request, HttpContext context)
    {
        if (string.IsNullOrWhiteSpace(request.Actor) || request.Actor.Length > 160 || request.Actor.Contains('\0'))
            throw new AeroSynapse.Application.Storage.ScenarioValidationException(new Dictionary<string, string[]> { ["actor"] = ["Actor label must contain 1–160 characters."] });
        var identity = context.User.Identity!.Name!;
        return request with { Actor = identity.Length <= 160 ? identity : context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value };
    }
    public static void MapStaffingEndpoints(this WebApplication app)
    {
        var pareto = app.MapGroup("/staffing-evaluations/{evaluationId:guid}/pareto").WithTags("Multi-objective staffing").AddEndpointFilter<ScenarioErrorFilter>();
        pareto.MapGet("/compare/{otherId:guid}", async (Guid evaluationId, Guid otherId, IBatchService service, CancellationToken token) =>
        {
            var left = await service.GetStaffingEvaluationAsync(evaluationId, token); var right = await service.GetStaffingEvaluationAsync(otherId, token);
            if (left.Pareto is null || right.Pareto is null) throw new AeroSynapse.Application.Storage.ScenarioConflictException("Both evaluations must capture multi-objective settings.");
            return Results.Ok(ParetoAnalysisPolicy.Compare(left, right));
        }).Document<ParetoFrontierComparison>("Compare retained approximate frontiers using raw minutes, operating points and risk");
        var reviews = app.MapGroup("/staffing-evaluations/{evaluationId:guid}/recommendation")
            .WithTags("Recommendation approval").AddEndpointFilter<ScenarioErrorFilter>();
        reviews.MapGet("", async (Guid evaluationId, IRecommendationReviewService service, CancellationToken token) =>
            Results.Ok(await service.GetForEvaluationAsync(evaluationId, token)))
            .Document<RecommendationOutcome>("Read a saved recommendation or the explicit no-superior-candidate outcome");
        reviews.MapPost("", async (Guid evaluationId, IRecommendationReviewService service, CancellationToken token) =>
            Results.Ok(await service.CreateAsync(evaluationId, token)))
            .Document<RecommendationOutcome>("Idempotently prepare an explainable recommendation from a validated evaluation");
        var decisions = app.MapGroup("/recommendation-reviews/{id:guid}")
            .WithTags("Recommendation approval").AddEndpointFilter<ScenarioErrorFilter>();
        decisions.MapPost("/approve", async (Guid id, RecommendationDecisionRequest request, HttpContext context, IRecommendationReviewService service, CancellationToken token) =>
            Results.Ok(await service.DecideAsync(id, "approved", AuthenticatedDecision(request, context), token)))
            .Document<RecommendationDecisionResult>("Explicitly approve once, changing only synthetic scenario staffing with atomic audit");
        decisions.MapPost("/reject", async (Guid id, RecommendationDecisionRequest request, HttpContext context, IRecommendationReviewService service, CancellationToken token) =>
            Results.Ok(await service.DecideAsync(id, "rejected", AuthenticatedDecision(request, context), token)))
            .Document<RecommendationDecisionResult>("Explicitly reject once without changing the scenario, with atomic audit");
        var group = app.MapGroup("/scenarios/{id}/staffing").WithTags("Staff redistribution").AddEndpointFilter<ScenarioErrorFilter>();
        group.MapGet("/settings", async (Guid id, ISender sender, CancellationToken token) =>
            Results.Ok(await sender.Send(new GetStaffingSettingsQuery(id), token)))
            .Document<StaffingSettings>("Read available staff and current checkpoint assignments from the captured layout");
        group.MapPut("/allocation", async (Guid id, StaffingAllocationRequest request, ISender sender, CancellationToken token) =>
            Results.Ok(await sender.Send(new ApplyStaffingCommand(id, request), token)))
            .Document<AeroSynapse.Application.Storage.ScenarioDetails>("Staff each checkpoint with a fixed count using the scenario revision; replaces any shift plan");
        group.MapPost("/proposals", async (Guid id, JsonElement payload, ISender sender, CancellationToken token) =>
            Results.Ok(await sender.Send(new ProposeStaffingQuery(id, ContractJson.Read<StaffingProposalRequest>(payload, "StaffingProposalRequest")), token)))
            .AddEndpointFilter<AeroSynapse.API.Audit.AuditCalculationFilter>().Accepts<StaffingProposalRequest>("application/json")
            .Document<StaffingSolveResult>("Solve a bounded CP-SAT staffing proposal without changing the scenario").ProducesProblem(502);
        group.MapPost("/evaluations", async (Guid id, CreateStaffingEvaluationRequest request, IBatchService service, CancellationToken token) =>
        {
            var value = await service.CreateStaffingEvaluationAsync(id, request, token);
            return Results.Accepted($"/staffing-evaluations/{value.Batch.Batch.Id}", value);
        }).Document<StaffingEvaluationDetails>("Generate distinct CP-SAT candidates and capture paired SimPy evaluations", 202).ProducesProblem(502);
        group.MapGet("/evaluations", async (Guid id, IBatchService service, CancellationToken token) =>
            Results.Ok(await service.ListStaffingEvaluationsAsync(id, token)))
            .Document<IReadOnlyList<BatchSummary>>("List retained staffing evaluations for this baseline scenario");
        app.MapGet("/staffing-evaluations/{evaluationId:guid}", async (Guid evaluationId, IBatchService service, CancellationToken token) =>
            Results.Ok(await service.GetStaffingEvaluationAsync(evaluationId, token)))
            .WithTags("Staff redistribution").AddEndpointFilter<ScenarioErrorFilter>()
            .Document<StaffingEvaluationDetails>("Read simulated staffing metrics, rejection reasons and explicit tradeoffs");
    }
}
