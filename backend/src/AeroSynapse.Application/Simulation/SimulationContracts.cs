using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Operations;

namespace AeroSynapse.Application.Simulation;

public sealed record StartSimulationRequest(int ScenarioRevision);
public sealed record SimulationExecution(Guid CommandId, Guid RunId, Guid ScenarioId, string Status,
    string CorrelationId, bool ResultsValid, string? ErrorCode, string? FailureReason);
public sealed record EngineDiagnostics(string InputHash, string? ResultHash, string EngineVersion, string EngineBuild, double EngineMilliseconds);
public sealed record EngineExecution(Guid RunId, string Status, SimulationResponse? Result, EngineProgress? Progress = null, EngineDiagnostics? Diagnostics = null);
public sealed record ExecutionControl(int TimeoutMilliseconds);

public interface ISimulationService
{
    Task<SimulationExecution> StartAsync(Guid scenarioId, Guid commandId, int revision, string correlationId, CancellationToken token);
    Task<SimulationExecution?> GetAsync(Guid commandId, CancellationToken token);
}

public interface IIntelligenceClient
{
    Task<EngineExecution> StartControlledAsync(SimulationRequest request, ExecutionControl control, string correlationId, CancellationToken token) => StartAsync(request, correlationId, token);
    Task CancelAsync(Guid runId, string correlationId, CancellationToken token) => Task.CompletedTask;
    Task<EngineExecution> StartAsync(SimulationRequest request, string correlationId, CancellationToken token);
    Task<EngineExecution?> GetAsync(Guid runId, string correlationId, CancellationToken token);
}

public sealed class IntelligenceException(string code, string message, bool transient = false) : Exception(message)
{
    public string Code { get; } = code;
    public bool Transient { get; } = transient;
}
