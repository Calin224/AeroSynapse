using AeroSynapse.Application.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;

namespace AeroSynapse.Infrastructure.Storage;

public static class StorageRegistration
{
    public static IServiceCollection AddPostgresStorage(this IServiceCollection services,
        PostgresSettings settings, bool seedSynthetic, Func<IServiceProvider, PostgresSettings>? tenantSettings = null)
    {
        services.AddDbContext<TwinDbContext>((provider, options) => Configure(options, tenantSettings?.Invoke(provider) ?? settings, seedSynthetic));
        services.AddScoped<ISceneQueryService, SceneQueryService>();
        services.AddScoped<IScenarioService, ScenarioService>();
        services.AddScoped<IRunStorageService, RunStorageService>();
        services.AddScoped<AeroSynapse.Application.Management.IOperationalNotifications, AeroSynapse.Infrastructure.Execution.OperationalNotifications>();
        services.AddScoped<AeroSynapse.Application.Management.IRecommendationReviewService, AeroSynapse.Infrastructure.Execution.RecommendationReviewService>();
        services.AddScoped<AeroSynapse.Application.Management.IAnomalyReviewStore, AeroSynapse.Infrastructure.Execution.AnomalyReviewStore>();
        services.AddScoped<AeroSynapse.Application.Operations.IOperationsReader, AeroSynapse.Infrastructure.Operations.OperationsReader>();
        services.AddScoped<AeroSynapse.Application.Operations.IRunProgressRecorder, AeroSynapse.Infrastructure.Operations.RunProgressRecorder>();
        services.AddScoped<AeroSynapse.Application.Management.IRunExecutionService, AeroSynapse.Infrastructure.Execution.RunExecutionService>();
        services.AddScoped<AeroSynapse.Application.Management.IRunComparisonService, AeroSynapse.Infrastructure.Execution.RunComparisonService>();
        services.AddScoped<AeroSynapse.Application.Management.IReplayService, AeroSynapse.Infrastructure.Execution.ReplayService>();
        services.AddScoped<AeroSynapse.Application.Management.ICopilotService, AeroSynapse.Infrastructure.Execution.CopilotService>();
        services.AddScoped<AeroSynapse.Application.Management.ICopilotTools, AeroSynapse.Infrastructure.Execution.CopilotTools>();
        services.AddScoped<AeroSynapse.Application.Management.ICopilotTranscriptStore, AeroSynapse.Infrastructure.Execution.CopilotTranscriptStore>();
        services.AddScoped<AeroSynapse.Application.Management.ICopilotWorkflows, AeroSynapse.Infrastructure.Execution.CopilotWorkflowService>();
        services.AddScoped<AeroSynapse.Infrastructure.Execution.CopilotWorkflowProcessor>();
        services.AddScoped<AeroSynapse.Application.Management.IBatchService, AeroSynapse.Infrastructure.Execution.BatchService>();
        services.AddScoped<AeroSynapse.Infrastructure.Execution.BatchProcessor>();
        services.AddHealthChecks().AddCheck<StorageReadiness>("storage", timeout: TimeSpan.FromSeconds(5));
        return services;
    }

    public static void Configure(DbContextOptionsBuilder options, PostgresSettings settings, bool seedSynthetic)
    {
        options.UseNpgsql(settings.ConnectionString(), postgres => postgres.MigrationsHistoryTable("__EFMigrationsHistory", "twin"));
        // EF coordinates these callbacks with its migration lock, including CLI operations.
        options.UseSeeding((context, _) =>
        {
            if (seedSynthetic && context.Database.GetAppliedMigrations().Any())
                SyntheticScenarioSeeder.Seed((TwinDbContext)context);
        });
        options.UseAsyncSeeding(async (context, _, cancellationToken) =>
        {
            if (seedSynthetic && (await context.Database.GetAppliedMigrationsAsync(cancellationToken)).Any())
                await SyntheticScenarioSeeder.SeedAsync((TwinDbContext)context, cancellationToken);
        });
    }
}

public sealed class TwinDbContextFactory : IDesignTimeDbContextFactory<TwinDbContext>
{
    public TwinDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<TwinDbContext>();
        StorageRegistration.Configure(options, PostgresSettings.FromEnvironment(),
            string.Equals(Environment.GetEnvironmentVariable("Database__SeedSynthetic"), "true", StringComparison.OrdinalIgnoreCase));
        return new TwinDbContext(options.Options);
    }
}
