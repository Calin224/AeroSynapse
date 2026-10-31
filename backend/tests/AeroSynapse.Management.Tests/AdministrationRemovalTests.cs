using System.Net;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AeroSynapse.Management.Tests;

public sealed class AdministrationRemovalTests
{
    [Fact]
    public async Task Retired_administration_and_exploration_endpoints_are_not_routed()
    {
        await using var host = await ManagementHost.CreateAsync();
        var routes = host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();
        Assert.DoesNotContain(routes, endpoint =>
        {
            var path = endpoint.RoutePattern.RawText?.TrimStart('/') ?? "";
            return path.StartsWith("admin/", StringComparison.Ordinal) || path == "audit"
                || path.Contains("/audit/", StringComparison.Ordinal)
                || path.Contains("adapters", StringComparison.Ordinal)
                || path.EndsWith("data-provenance", StringComparison.Ordinal)
                || path.Contains("passenger-feedback", StringComparison.Ordinal)
                || path.Contains("evacuation", StringComparison.Ordinal);
        });
        // The schema route is generic, so the retired definition is checked by name.
        using var schema = await host.Client.GetAsync("/contracts/v1/schemas/EvacuationRequest", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, schema.StatusCode);
    }
}
