using System.Text.Json;
using AeroSynapse.Application.Contracts;

namespace AeroSynapse.API;

public static class ContractEndpoints
{
    public static void MapContractEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/contracts/v1").AllowAnonymous();
        foreach (var (path, definition) in new[]
        {
            ("journeys/response/validate", "JourneySimulationResponse"),
            ("simulation/validate", "SimulationRequest"), ("optimization/validate", "OptimizationRequest"),
            ("resources/validate", "ResourceSolveRequest"), ("resources/result/validate", "ResourceSolveResult"),
            ("forecast/validate", "QueueForecastRequest"), ("forecast/result/validate", "QueueForecastResult"), ("forecast/prediction/validate", "QueuePredictionResult"),
            ("anomaly/validate", "AnomalyDetectionRequest"), ("anomaly/result/validate", "AnomalyDetectionResult"),
            ("anomaly/evaluation/result/validate", "AnomalyEvaluationResult"), ("anomaly/case/result/validate", "AnomalyCaseResult"),
            ("staffing/validate", "StaffingSolveRequest"), ("staffing/result/validate", "StaffingSolveResult"),
            ("staffing/candidates/validate", "StaffingCandidateSearchRequest"), ("staffing/candidates/result/validate", "StaffingCandidateSearchResult"),
            ("scenario/validate", "ScenarioSnapshot"), ("zone/validate", "ZoneSnapshot"),
            ("simulation/response/validate", "SimulationResponse"), ("optimization/response/validate", "OptimizationResponse"),
            ("simulation/result/validate", "SimulationResult"), ("optimization/result/validate", "OptimizationResult"),
            ("passengers/validate", "PassengerGenerationRequest"), ("passengers/simulation/validate", "ProfiledSimulationRequest"),
            ("navigation/validate", "NavigationPreviewRequest"), ("navigation/simulation/validate", "RoutedSimulationRequest"), ("navigation/result/validate", "NavigationResult"),
            ("processes/simulation/validate", "SimPySimulationRequest"), ("processes/result/validate", "ProcessResult"),
            ("kpis/simulation/validate", "KpiSimulationRequest"), ("kpis/result/validate", "KpiResult"), ("kpis/snapshot/validate", "KpiSnapshot")
        })
            group.MapPost("/" + path, (HttpRequest request, CancellationToken cancellationToken) => Validate(request, definition, cancellationToken))
                .Produces<JsonElement>().Produces<ContractErrorResponse>(422).Produces<ContractErrorResponse>(415);
        group.MapGet("/schemas/{definition}", (string definition) =>
            definition is "ScenarioSnapshot" or "ZoneSnapshot" or "SimulationResponse" or "OptimizationResponse"
                or "SimulationRequest" or "OptimizationRequest" or "SimulationResult" or "OptimizationResult" or "ValidationError"
                or "PassengerGenerationRequest" or "PassengerGenerationConfig" or "ProfiledSimulationRequest" or "ProfiledScenarioSnapshot"
                or "NavigationConfig" or "NavigationPreviewRequest" or "NavigationResult" or "RoutedSimulationRequest" or "RoutedSimulationResponse"
                or "SimPySimulationRequest" or "SimPyScenarioSnapshot" or "SimPySimulationResponse" or "ProcessResult"
                or "KpiSimulationRequest" or "KpiScenarioSnapshot" or "KpiSimulationResponse" or "KpiResult" or "KpiSnapshot"
                or "JourneyInput" or "JourneyResult" or "JourneySimulationResponse"
                or "IntegratedResourceConfig" or "ResourceSolveRequest" or "ResourceSolveResult"
                or "QueueForecastOptions" or "QueueForecastLiveOptions" or "QueueForecastRequest" or "QueueForecastResult" or "QueuePredictionResult"
                or "AnomalySettings" or "AnomalyDetectionRequest" or "AnomalyDetectionResult" or "AnomalyEvaluationRequest"
                or "AnomalyEvaluationResult" or "AnomalyCaseRequest" or "AnomalyCaseResult"
                or "StaffingSolveRequest" or "StaffingSolveResult" or "StaffingProposalRequest"
                or "StaffingCandidateSearchRequest" or "StaffingCandidateSearchResult"
                ? (IResult)Results.Json(VersionedContract.Schema(definition)) : Results.NotFound());
        group.MapGet("/openapi.json", () =>
        {
            using var stream = typeof(VersionedContract).Assembly.GetManifestResourceStream("AeroSynapse.Contracts.v1.openapi.json")!;
            using var document = JsonDocument.Parse(stream);
            return Results.Json(document.RootElement.Clone());
        });
    }

    private static async Task<IResult> Validate(HttpRequest request, string definition, CancellationToken cancellationToken)
    {
        if (request.ContentType?.Split(';')[0].Trim().Equals("application/json", StringComparison.OrdinalIgnoreCase) != true)
            return Results.Json(VersionedContract.Response([VersionedContract.Error("", "unsupported_media_type")]), statusCode: 415);
        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);
            var errors = VersionedContract.Validate(definition, document.RootElement);
            return errors.Count == 0 ? Results.Json(document.RootElement.Clone())
                : Results.Json(VersionedContract.Response(errors), statusCode: 422);
        }
        catch (JsonException)
        {
            return Results.Json(VersionedContract.Response([VersionedContract.Error("", "malformed_json")]), statusCode: 422);
        }
    }
}
