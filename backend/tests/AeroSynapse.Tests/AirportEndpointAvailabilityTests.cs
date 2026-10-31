using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace AeroSynapse.Tests;

public sealed class AirportEndpointAvailabilityTests
{
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Testing")]
    public async Task Map_routes_are_unavailable_outside_development_and_health_needs_no_database(string environment)
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment(environment);
                builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                    new Dictionary<string, string?> { ["ConnectionStrings:AirportMaps"] = "invalid-connection-string" }));
            });
        using var client = factory.CreateClient();

        using var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        using var list = await client.GetAsync("/api/airports");
        Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
        using var create = await client.PostAsJsonAsync("/api/airports", MapFixture.Create(), MapFixture.Json);
        Assert.Equal(HttpStatusCode.NotFound, create.StatusCode);
        using var snapshot = await client.GetAsync($"/api/airports/{Guid.NewGuid()}/snapshot");
        Assert.Equal(HttpStatusCode.NotFound, snapshot.StatusCode);
        using var navigation = await client.GetAsync($"/api/airports/{Guid.NewGuid()}/navigation");
        Assert.Equal(HttpStatusCode.NotFound, navigation.StatusCode);
        using var validation = await client.PostAsJsonAsync("/api/airports/validate", MapFixture.Create(), MapFixture.Json);
        Assert.Equal(HttpStatusCode.NotFound, validation.StatusCode);
    }
}
