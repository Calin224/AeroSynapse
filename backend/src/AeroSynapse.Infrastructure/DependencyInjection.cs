using AeroSynapse.Application.Airports;
using AeroSynapse.Infrastructure.Airports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.EntityFrameworkCore;

namespace AeroSynapse.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, string connectionString, Func<IServiceProvider, string>? tenantConnection = null)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddDbContext<AirportMapDbContext>((provider, options) => options.UseSqlite(tenantConnection?.Invoke(provider) ?? connectionString));
        services.AddScoped<IAirportMapRepository, AirportMapRepository>();

        return services;
    }
}
