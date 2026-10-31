using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AeroSynapse.Application.Airports;
using AeroSynapse.Domain.Airports;
using AeroSynapse.Infrastructure.Airports;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using AeroSynapse.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using System.Security.Cryptography;

namespace AeroSynapse.Tests;

public sealed class AirportMapApiTests : IAsyncLifetime
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"aerosynapse-api-{Guid.NewGuid():N}.db");
    private WebApplicationFactory<Program> factory = null!;
    private HttpClient client = null!;

    public async Task InitializeAsync()
    {
        factory = new AirportApiFactory(path);
        client = factory.CreateClient();
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) + "aA1!";
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<WorkspaceUser>>();
            var account = new WorkspaceUser { UserName = "airport-test" };
            Assert.True((await users.CreateAsync(account, password)).Succeeded);
            await users.AddToRoleAsync(account, "Admin");
        }
        var csrf = await client.GetFromJsonAsync<JsonElement>("/auth/csrf");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf.GetProperty("requestToken").GetString());
        using var login = await client.PostAsJsonAsync("/auth/login", new { username = "airport-test", password });
        login.EnsureSuccessStatusCode();
        csrf = await client.GetFromJsonAsync<JsonElement>("/auth/csrf");
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf.GetProperty("requestToken").GetString());
    }

    public async Task DisposeAsync()
    {
        client.Dispose();
        await factory.DisposeAsync();
        foreach (var suffix in new[] { "", "-shm", "-wal" }) { File.Delete(path + suffix); File.Delete(path + ".identity" + suffix); }
    }

    [Theory]
    [InlineData("{\"seed\":1,\"seed\":2,\"seed\":3}", "/seed", "duplicate_field")]
    [InlineData("{\"checkpoints\":[{\"staffCount\":1,\"staffCount\":2}]}", "/checkpoints/0/staffCount", "duplicate_field")]
    [InlineData("{\"durationMinutes\":", "", "malformed_json")]
    public async Task Security_middleware_preserves_versioned_contract_wire_errors(string body, string field, string code)
    {
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/contracts/v1/simulation/validate", content);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(result.GetProperty("errors").EnumerateArray(), error =>
            error.GetProperty("field").GetString() == field && error.GetProperty("code").GetString() == code);
    }

    [Fact]
    public async Task Security_middleware_keeps_the_shared_contract_nesting_limit()
    {
        var body = "{\"unknown\":" + new string('[', 63) + new string(']', 63) + "}";
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/contracts/v1/simulation/validate", content);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.DoesNotContain(result.GetProperty("errors").EnumerateArray(), error =>
            error.GetProperty("code").GetString() == "malformed_json");
    }

    [Fact]
    public async Task Create_and_read_preserve_the_entire_map_in_a_versioned_snapshot()
    {
        var map = MapFixture.Create();
        using var created = await client.PostAsJsonAsync("/api/airports", map, MapFixture.Json);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal($"/api/airports/{map.Airport.Id}/snapshot", created.Headers.Location?.OriginalString);

        using var exported = await client.GetAsync(created.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, exported.StatusCode);
        var snapshot = await exported.Content.ReadFromJsonAsync<AirportMapSnapshot>(MapFixture.Json);
        Assert.NotNull(snapshot);
        Assert.True(snapshot.IsNavigable);
        Assert.Empty(snapshot.Issues);
        Assert.Equal(1, snapshot.SchemaVersion);

        // Compare every field of every object, independently of collection order.
        var expected = map.Elements().ToDictionary(item => item.Id,
            item => JsonSerializer.Serialize(item, item.GetType(), MapFixture.Json));
        var actual = snapshot.Map.Elements().ToDictionary(item => item.Id,
            item => JsonSerializer.Serialize(item, item.GetType(), MapFixture.Json));
        Assert.Equal(expected.Count, actual.Count);
        foreach (var (id, json) in expected) Assert.Equal(json, actual[id]);

        using var document = JsonDocument.Parse(await exported.Content.ReadAsStringAsync());
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("Entrance", document.RootElement.GetProperty("map").GetProperty("zones")
            .EnumerateArray().Single(zone => zone.GetProperty("id").GetGuid() == map.Zones[0].Id)
            .GetProperty("type").GetString());
    }

    [Theory]
    [InlineData("airport", "cross_airport_reference")]
    [InlineData("terminal", "cross_terminal_reference")]
    [InlineData("pool", "missing_resource_pool")]
    [InlineData("capacity", "invalid_value")]
    [InlineData("rate", "invalid_value")]
    [InlineData("duration", "invalid_value")]
    [InlineData("unreachable", "unreachable_zone")]
    [InlineData("disconnectedEntrances", "disconnected_zone")]
    [InlineData("checkpoint", "checkpoint_unavailable")]
    public async Task Invalid_maps_return_actionable_issues_without_storing_an_airport(string mutation, string code)
    {
        var map = MapFixture.Create();
        switch (mutation)
        {
            case "airport": map.Zones[1].AirportId = Guid.NewGuid(); break;
            case "terminal":
                var other = new Terminal { Id = Guid.NewGuid(), AirportId = map.Airport.Id, Name = "Other terminal", IsActive = false };
                map.Terminals.Add(other);
                map.Routes[0].TerminalId = other.Id;
                break;
            case "pool": map.Checkpoints[0].ResourcePoolId = Guid.NewGuid(); break;
            case "capacity": map.Zones[1].Capacity = 0; break;
            case "rate": map.Gates[0].BoardingRatePerMinute = -1; break;
            case "duration": map.Routes[0].TravelDurationSeconds = 0; break;
            case "unreachable": map.Routes.RemoveAt(1); break;
            case "disconnectedEntrances": map.Zones[2].Type = ZoneType.Entrance; map.Routes.RemoveAt(1); break;
            case "checkpoint": map.Checkpoints[0].IsActive = false; break;
        }

        using var response = await client.PostAsJsonAsync("/api/airports", map, MapFixture.Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var issue = document.RootElement.GetProperty("issues").EnumerateArray()
            .First(item => item.GetProperty("code").GetString() == code);
        Assert.NotEqual(Guid.Empty, issue.GetProperty("objectId").GetGuid());
        Assert.False(string.IsNullOrWhiteSpace(issue.GetProperty("field").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(issue.GetProperty("message").GetString()));
        Assert.Equal("Error", issue.GetProperty("severity").GetString());
        using var exported = await client.GetAsync($"/api/airports/{map.Airport.Id}/snapshot");
        Assert.Equal(HttpStatusCode.NotFound, exported.StatusCode);
    }

    [Theory]
    [InlineData("1e400")]
    [InlineData("-1e400")]
    [InlineData("\"NaN\"")]
    [InlineData("\"Infinity\"")]
    [InlineData("\"-Infinity\"")]
    public async Task Nonfinite_coordinates_are_rejected(string value)
    {
        var map = MapFixture.Create();
        var json = JsonSerializer.Serialize(map, MapFixture.Json).Replace("\"x\":0", $"\"x\":{value}");
        using var body = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/api/airports", body);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains(document.RootElement.GetProperty("issues").EnumerateArray(),
            issue => issue.GetProperty("code").GetString() == "invalid_coordinate"
                && issue.GetProperty("field").GetString() == "x");
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"zones\":[{\"type\":\"Unsupported\"}]}")]
    [InlineData("{\"zones\":[{\"x\":\"not-a-number\"}]}")]
    [InlineData("{\"airport\":{\"id\":\"invalid-guid\"}}")]
    public async Task Malformed_request_bodies_return_bad_request(string json)
    {
        using var body = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/api/airports", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Existing_ids_return_conflict_even_when_reused_by_a_different_entity_type()
    {
        var map = MapFixture.Create();
        using var created = await client.PostAsJsonAsync("/api/airports", map, MapFixture.Json);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var duplicate = await client.PostAsJsonAsync("/api/airports", map, MapFixture.Json);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        var another = MapFixture.Create();
        another.Gates[0].Id = map.ResourcePools[0].Id;
        using var collision = await client.PostAsJsonAsync("/api/airports", another, MapFixture.Json);
        Assert.Equal(HttpStatusCode.Conflict, collision.StatusCode);
        using var exported = await client.GetAsync($"/api/airports/{another.Airport.Id}/snapshot");
        Assert.Equal(HttpStatusCode.NotFound, exported.StatusCode);
    }

    [Fact]
    public async Task Inactive_leaf_warnings_are_preserved_on_export()
    {
        var map = MapFixture.Create();
        map.Zones[2].IsActive = false;
        using var created = await client.PostAsJsonAsync("/api/airports", map, MapFixture.Json);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var snapshot = await client.GetFromJsonAsync<AirportMapSnapshot>(created.Headers.Location, MapFixture.Json);
        Assert.NotNull(snapshot);
        Assert.True(snapshot.IsNavigable);
        var issue = Assert.Single(snapshot.Issues);
        Assert.Equal("inactive_zone", issue.Code);
        Assert.Equal(map.Zones[2].Id, issue.ObjectId);
        Assert.Equal(MapIssueSeverity.Warning, issue.Severity);
    }

    [Fact]
    public async Task Unknown_airport_returns_not_found()
    {
        using var response = await client.GetAsync($"/api/airports/{Guid.NewGuid()}/snapshot");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private sealed class AirportApiFactory(string path) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Postgres:Host"] = "" }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<AirportMapDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<AirportMapDbContext>>();
                services.AddDbContext<AirportMapDbContext>(options => options.UseSqlite($"Data Source={path};Pooling=False"));
                services.RemoveAll<DbContextOptions<IdentityStore>>();
                services.RemoveAll<IDbContextOptionsConfiguration<IdentityStore>>();
                services.AddDbContext<IdentityStore>(options => options.UseSqlite($"Data Source={path}.identity;Pooling=False"));
            });
        }
    }
}
