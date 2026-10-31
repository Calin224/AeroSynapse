using System.Net.Http.Json;
using System.Text.Json;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Management;
using AeroSynapse.Application.Simulation;

namespace AeroSynapse.Infrastructure.Execution;

/// <summary>Runs the request up to a minute in the Python engine and reads the state the agent decides on.</summary>
public sealed class AgentEngineClient(HttpClient http) : IAgentEngine
{
    public async Task<AgentObservation> ObserveAsync(SimulationRequest request, int minute, Guid? gateEventId, AgentLimits? limits, CancellationToken token)
    {
        try
        {
            using var response = await http.PostAsJsonAsync("simulation/v1/observe", new { request, minute, gateEventId, limits }, ContractJson.Options, token);
            response.EnsureSuccessStatusCode();
            var observation = await response.Content.ReadFromJsonAsync<AgentObservation>(ContractJson.Options, token)
                ?? throw new JsonException("Empty observation.");
            var controls = request.Checkpoints.Select(c => c.CheckpointId).ToHashSet();
            if (observation.ObservationVersion != "1.0" || observation.Minute != minute
                || !controls.SetEquals(observation.Checkpoints.Select(c => c.CheckpointId))
                || (gateEventId is not null && observation.Gate?.EventId != gateEventId))
                throw new JsonException("Observation does not match the request.");
            return observation;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception failure) when (failure is HttpRequestException or JsonException or OperationCanceledException or NotSupportedException)
        { throw new IntelligenceException("agent_observe_failed", $"The engine could not pause the run at minute {minute} for the agent."); }
    }
}
