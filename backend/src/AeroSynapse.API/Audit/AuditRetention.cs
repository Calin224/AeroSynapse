using AeroSynapse.Infrastructure.Audit;
using AeroSynapse.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AeroSynapse.API.Audit;

internal sealed class AuditRetention(IServiceScopeFactory scopes, IOptions<AuditOptions> options, ILogger<AuditRetention> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var database = scope.ServiceProvider.GetRequiredService<TwinDbContext>();
                await database.Database.ExecuteSqlInterpolatedAsync($"SELECT twin.ai_agent_expire({options.Value.RetentionDays})", token);
                await database.Database.ExecuteSqlInterpolatedAsync($"SELECT twin.external_data_expire({options.Value.RetentionDays})", token);
                await database.Database.ExecuteSqlInterpolatedAsync($"SELECT twin.audit_expire({options.Value.RetentionDays})", token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception error) { logger.LogWarning("Audit retention will retry errorType={ErrorType}", error.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromHours(6), token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        }
    }
}
