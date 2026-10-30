using AeroSynapse.Application.Abstractions;
using AeroSynapse.Infrastructure.Time;
using Microsoft.Extensions.DependencyInjection;

namespace AeroSynapse.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IClock, SystemClock>();

        return services;
    }
}
