using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace AeroSynapse.Infrastructure.Health;

public static class PostgresReadinessRegistration
{
    public static IServiceCollection AddPostgresReadiness(this IServiceCollection services, PostgresSettings settings)
    {
        services.AddSingleton(_ => NpgsqlDataSource.Create(settings.ConnectionString(3)));
        services.AddHealthChecks().AddCheck<PostgresReadiness>("postgres", timeout: TimeSpan.FromSeconds(5));
        return services;
    }
}

internal sealed class PostgresReadiness(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var command = dataSource.CreateCommand("SELECT 1");
            await command.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception error) when (error is NpgsqlException or TimeoutException or OperationCanceledException)
        {
            // Do not include connection strings or server diagnostics in health responses/logs.
            return HealthCheckResult.Unhealthy("PostgreSQL is unavailable.");
        }
    }
}
