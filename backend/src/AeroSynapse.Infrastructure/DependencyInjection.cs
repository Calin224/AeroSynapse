using AeroSynapse.Application.Abstractions;
using AeroSynapse.Application.Airports;
using AeroSynapse.Infrastructure.Airports;
using AeroSynapse.Infrastructure.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroSynapse.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services,
        string connectionString = "Data Source=aerosynapse.db")
    {
        services.AddSingleton<IClock, SystemClock>();
        services.AddDbContext<AirportMapDbContext>(options => options.UseSqlite(connectionString));
        services.AddScoped<IAirportMapRepository, AirportMapRepository>();

        return services;
    }
}
