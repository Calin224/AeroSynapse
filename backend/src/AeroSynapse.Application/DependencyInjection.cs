using AeroSynapse.Application.Health;
using Microsoft.Extensions.DependencyInjection;

namespace AeroSynapse.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddMediatR(configuration =>
            configuration.RegisterServicesFromAssemblyContaining<GetHealthQuery>());

        return services;
    }
}
