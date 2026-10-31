using AeroSynapse.Application.Health;
using Microsoft.Extensions.DependencyInjection;

namespace AeroSynapse.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services, bool includeOperationalManagement = false)
    {
        services.AddMediatR(configuration =>
        {
            configuration.TypeEvaluator = type => includeOperationalManagement || type.Namespace != "AeroSynapse.Application.Management";
            configuration.RegisterServicesFromAssemblyContaining<GetHealthQuery>();
        });

        return services;
    }
}
