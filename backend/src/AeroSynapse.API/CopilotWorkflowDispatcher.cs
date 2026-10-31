using AeroSynapse.Infrastructure.Execution;

namespace AeroSynapse.API;

internal sealed class CopilotWorkflowDispatcher(IServiceScopeFactory scopes, ILogger<CopilotWorkflowDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                if (await scope.ServiceProvider.GetRequiredService<CopilotWorkflowProcessor>().ProcessNextAsync(stoppingToken)) continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogWarning("Playbook dispatcher will recover errorType={ErrorType}", error.GetType().Name); }
            try { await Task.Delay(200, stoppingToken); } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
