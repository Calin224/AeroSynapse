using AeroSynapse.Application.Management;

namespace AeroSynapse.API;

internal sealed class CopilotTranscriptCleanup(IServiceScopeFactory scopes, ILogger<CopilotTranscriptCleanup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        // Wait until startup migrations finish. Reads already exclude expired records.
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ICopilotTranscriptStore>().PurgeExpiredAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogWarning("Copilot transcript cleanup failed: {ErrorType}", error.GetType().Name); }
        }
    }
}
