using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroSynapse.Application.Contracts;

namespace AeroSynapse.Infrastructure.Simulation;

public sealed class NavigationPlannerClient(HttpClient http) : INavigationPlanner
{
    public async Task<NavigationResult> PreviewAsync(NavigationPreviewRequest request,int offset,int limit,CancellationToken token)
    {
        try
        {
            using var response = await http.PostAsJsonAsync($"navigation/v1/preview?offset={offset}&limit={limit}",request,ContractJson.Options,token);
            if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
            {
                var fields = await response.Content.ReadFromJsonAsync<ContractErrorResponse>(ContractJson.Options,token);
                if (fields is not null) throw new ContractValidationException(fields.Errors);
            }
            response.EnsureSuccessStatusCode();
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token),cancellationToken:token);
            NavigationResult result;
            try { result = ContractJson.Read<NavigationResult>(json.RootElement,"NavigationResult"); }
            catch (ContractValidationException failure) { throw new JsonException("Invalid navigation response.",failure); }
            if (!NavigationCorrelation.Valid(request.PassengerGeneration,request.Navigation,result,false,offset,limit)) throw new JsonException("Uncorrelated navigation response.");
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception failure) when (failure is JsonException or HttpRequestException or OperationCanceledException) { throw new NavigationPlannerUnavailableException(); }
    }
}
