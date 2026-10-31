using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroSynapse.Application.Contracts;

namespace AeroSynapse.Infrastructure.Simulation;

public sealed class StaffingPlannerClient(HttpClient http) : IStaffingPlanner
{
    public Task<ParetoSearchResult> GenerateParetoAsync(ParetoSearchRequest request, CancellationToken token) =>
        Post<ParetoSearchRequest, ParetoSearchResult>(request, "pareto", "ParetoSearchResult", result => ParetoModel.ValidResult(request, result), token);
    public Task<StaffingSolveResult> SolveAsync(StaffingSolveRequest request, CancellationToken token) =>
        Post<StaffingSolveRequest, StaffingSolveResult>(request, "solve", "StaffingSolveResult", result => StaffingValidation.Valid(request, result), token);

    public Task<StaffingCandidateSearchResult> GenerateCandidatesAsync(StaffingCandidateSearchRequest request, CancellationToken token) =>
        Post<StaffingCandidateSearchRequest, StaffingCandidateSearchResult>(request, "candidates", "StaffingCandidateSearchResult", result => StaffingValidation.ValidCandidates(request, result), token);

    public Task<ShiftPlanResult> PlanShiftsAsync(ShiftPlanRequest request, CancellationToken token) =>
        Post<ShiftPlanRequest, ShiftPlanResult>(request, "shifts", "ShiftPlanResult", result =>
            JsonElement.DeepEquals(JsonSerializer.SerializeToElement(request, ContractJson.Options), JsonSerializer.SerializeToElement(result.Request, ContractJson.Options))
            && ShiftPlanValidation.Valid(result), token);

    private async Task<TResponse> Post<TRequest, TResponse>(TRequest request, string operation, string definition,
        Func<TResponse, bool> valid, CancellationToken token)
    {
        try
        {
            using var response = await http.PostAsJsonAsync("optimization/staffing/v1/" + operation, request, ContractJson.Options, token);
            if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
            {
                var fields = await response.Content.ReadFromJsonAsync<ContractErrorResponse>(ContractJson.Options, token);
                if (fields is { Errors.Count: > 0 }) throw new ContractValidationException(fields.Errors);
            }
            response.EnsureSuccessStatusCode();
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
            TResponse result;
            try { result = ContractJson.Read<TResponse>(json.RootElement, definition); }
            catch (ContractValidationException failure) { throw new JsonException("Invalid staffing proposal.", failure); }
            if (!valid(result)) throw new JsonException("Uncorrelated staffing proposal.");
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception failure) when (failure is JsonException or HttpRequestException or OperationCanceledException)
        { throw new StaffingPlannerUnavailableException(); }
    }
}
