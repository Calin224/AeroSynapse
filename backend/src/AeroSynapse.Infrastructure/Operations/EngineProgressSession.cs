using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Operations;
using AeroSynapse.Application.Simulation;

namespace AeroSynapse.Infrastructure.Operations;

public static class EngineProgressSession
{
    // Exactly one POST. Progress lookups are safe GETs while the response is outstanding.
    public static async Task<EngineExecution> StartAsync(IIntelligenceClient client, SimulationRequest request,
        string correlationId, IRunProgressRecorder recorder, int pollMilliseconds, CancellationToken token, ExecutionControl? control = null)
    {
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var started = control is null ? client.StartAsync(request, correlationId, requestCancellation.Token)
            : client.StartControlledAsync(request, control, correlationId, requestCancellation.Token);
        try
        {
            EngineExecution? confirmed = null;
            while (!started.IsCompleted)
            {
                if (await Task.WhenAny(started, Task.Delay(pollMilliseconds, token)) == started) break;
                token.ThrowIfCancellationRequested();
                try
                {
                    var frame = await client.GetAsync(request.RunId, correlationId, token);
                    await RecordAsync(recorder, frame, token);
                    if (frame?.Result is not null) { confirmed = frame; requestCancellation.Cancel(); break; }
                }
                catch (IntelligenceException error) when (error.Transient) { /* Original POST still determines dispatch outcome. */ }
            }
            EngineExecution result;
            try { result = await started; }
            catch (OperationCanceledException) when (confirmed is not null && !token.IsCancellationRequested) { result = confirmed; }
            catch (IntelligenceException error) when (confirmed is not null && error.Transient) { result = confirmed; }
            await RecordAsync(recorder, result, token);
            return result;
        }
        finally
        {
            // A rejected progress frame must also release the outstanding HTTP request.
            // Cancellation stops waiting; Python keeps its durable execution receipt.
            requestCancellation.Cancel();
            try { await started; }
            catch (Exception) { /* Observe dispatch faults without replacing the original error. */ }
        }
    }

    public static async Task RecordAsync(IRunProgressRecorder recorder, EngineExecution? execution, CancellationToken token)
    {
        if (execution?.Progress is not { } progress) return;
        try { await recorder.RecordAsync(execution.RunId, progress, token); }
        catch (InvalidOperationException) { throw new IntelligenceException("engine_invalid_response", "Engine progress does not match the captured run."); }
    }
}
