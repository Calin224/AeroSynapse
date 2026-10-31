using System.Net.Http.Json;
using System.Text.Json;
using AeroSynapse.Application.Contracts;

namespace AeroSynapse.Infrastructure.Simulation;

public sealed class ResourcePlannerClient(HttpClient http) : IResourcePlanner
{
    public async Task<ResourceSolveResult> SolveAsync(ResourceSolveRequest request, CancellationToken token)
    {
        try
        {
            using var response = await http.PostAsJsonAsync("optimization/resources/v1/solve", request, ContractJson.Options, token);
            response.EnsureSuccessStatusCode();
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
            ResourceSolveResult result;
            try { result = ContractJson.Read<ResourceSolveResult>(document.RootElement, "ResourceSolveResult"); }
            catch (ContractValidationException failure) { throw new JsonException("Invalid integrated resource proposal.", failure); }
            if (!ResourceValidation.Valid(request, result)) throw new JsonException("Uncorrelated integrated resource proposal.");
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception failure) when (failure is JsonException or HttpRequestException or OperationCanceledException)
        { throw new ResourcePlannerUnavailableException(); }
    }
}
