using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Management;
using AeroSynapse.Application.Operations;
using AeroSynapse.Application.Simulation;
using AeroSynapse.Infrastructure.Operations;
using AeroSynapse.Infrastructure.Simulation;
using Microsoft.Extensions.Options;
namespace AeroSynapse.Infrastructure.Execution;

public sealed class HttpSimulationEngine(IIntelligenceClient client, IRunProgressRecorder recorder,
    IOptions<OperationsOptions> operations, IOptions<SimulationOptions> settings) : ISimulationEngine
{
    public EngineDiagnostics? Diagnostics { get; private set; }
    public Task<SimulationResponse> ExecuteAsync(SimulationRequest request, CancellationToken token) => Execute(request, null, token);
    public Task<SimulationResponse> ExecuteControlledAsync(SimulationRequest request, ExecutionControl control, CancellationToken token) => Execute(request, control, token);
    private async Task<SimulationResponse> Execute(SimulationRequest request, ExecutionControl? control, CancellationToken token)
    {
        Diagnostics = null;
        var correlationId = request.RunId.ToString("N");
        var deadline = DateTime.UtcNow.AddSeconds(settings.Value.ReconciliationSeconds);
        try
        {
            EngineExecution? execution;
            try { execution = await EngineProgressSession.StartAsync(client, request, correlationId, recorder, operations.Value.EnginePollMilliseconds, token, control); }
            catch (IntelligenceException error) when (error.Transient) { execution = null; }
            while (execution?.Result is null)
            {
                if (DateTime.UtcNow >= deadline) throw new SimulationEngineException("engine_timeout", "Engine result was not confirmed before the reconciliation deadline.");
                await Task.Delay(operations.Value.EnginePollMilliseconds, token);
                try { execution = await client.GetAsync(request.RunId, correlationId, token); }
                catch (IntelligenceException error) when (error.Transient) { continue; }
                await EngineProgressSession.RecordAsync(recorder, execution, token);
            }
            Diagnostics = execution.Diagnostics;
            return execution.Result;
        }
        catch (IntelligenceException error) { throw new SimulationEngineException(error.Code, error.Message); }
    }
}
