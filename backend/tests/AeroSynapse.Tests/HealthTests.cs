using System.Net;
using System.Net.Http.Json;
using AeroSynapse.Application.Abstractions;
using AeroSynapse.Application.Health;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AeroSynapse.Tests;

public sealed class HealthTests
{
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task Api_starts_and_returns_current_utc_health_without_external_services(string environment)
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment(environment));
        using var client = factory.CreateClient();
        var before = DateTimeOffset.UtcNow;

        using var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var health = await response.Content.ReadFromJsonAsync<HealthResponse>();
        Assert.NotNull(health);
        Assert.Equal("Healthy", health.Status);
        Assert.Equal(TimeSpan.Zero, health.TimestampUtc.Offset);
        Assert.InRange(health.TimestampUtc, before, DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Health_uses_the_application_clock_through_dependency_injection()
    {
        var timestamp = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.RemoveAll<IClock>();
                services.AddSingleton<IClock>(new FixedClock(timestamp));
            }));
        using var client = factory.CreateClient();

        var health = await client.GetFromJsonAsync<HealthResponse>("/health");

        Assert.Equal(new HealthResponse("Healthy", timestamp), health);
    }

    private sealed class FixedClock(DateTimeOffset timestamp) : IClock
    {
        public DateTimeOffset UtcNow => timestamp;
    }
}
