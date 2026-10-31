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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AeroSynapse.Tests;

public sealed class AirportMapApiTests : IAsyncLifetime
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"aerosynapse-api-{Guid.NewGuid():N}.db");
    private WebApplicationFactory<Program> factory = null!;
    private HttpClient client = null!;

    public Task InitializeAsync()
    {
        factory = new AirportApiFactory(path);
        client = factory.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        client.Dispose();
        await factory.DisposeAsync();
        foreach (var suffix in new[] { "", "-shm", "-wal" }) File.Delete(path + suffix);
    }

    [Fact]
    public async Task Airport_list_is_empty_before_creation_and_ordered_after_creation()
    {
        Assert.Empty((await client.GetFromJsonAsync<Airport[]>("/api/airports"))!);
        var second = MapFixture.Create();
        second.Airport.Name = "Zulu Airport";
        var first = MapFixture.Create();
        first.Airport.Name = "Alpha Airport";
        using var createdSecond = await client.PostAsJsonAsync("/api/airports", second, MapFixture.Json);
        using var createdFirst = await client.PostAsJsonAsync("/api/airports", first, MapFixture.Json);
        Assert.Equal(HttpStatusCode.Created, createdSecond.StatusCode);
        Assert.Equal(HttpStatusCode.Created, createdFirst.StatusCode);

        var airports = await client.GetFromJsonAsync<Airport[]>("/api/airports");
        Assert.NotNull(airports);
        Assert.Equal(new[] { first.Airport.Id, second.Airport.Id }, airports.Select(item => item.Id));
        Assert.Equal(new[] { "Alpha Airport", "Zulu Airport" }, airports.Select(item => item.Name));
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
    [InlineData("{\"airport\":")]
    [InlineData("{\"unknown\":true}")]
    [InlineData("{\"zones\":[{\"type\":1}]}")]
    public async Task Malformed_request_bodies_return_bad_request(string json)
    {
        using var body = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/api/airports", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("airport", "null")]
    [InlineData("zones", "null")]
    [InlineData("zones", "[null]")]
    public async Task Missing_objects_or_null_collections_return_validation_issues(string field, string value)
    {
        using var body = new StringContent($"{{\"{field}\":{value}}}", Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/api/airports", body);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var document = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(document.GetProperty("issues").EnumerateArray(), issue =>
            issue.GetProperty("code").GetString() == "required" && issue.GetProperty("field").GetString() == field);
        Assert.Empty((await client.GetFromJsonAsync<Airport[]>("/api/airports"))!);
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

    [Fact]
    public async Task Navigation_endpoint_exports_the_stored_map_as_a_graph()
    {
        var map = MapFixture.Create();
        using var created = await client.PostAsJsonAsync("/api/airports", map, MapFixture.Json);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var response = await client.GetAsync($"/api/airports/{map.Airport.Id}/navigation");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var graph = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("1.0", graph.GetProperty("navigationVersion").GetString());
        Assert.Equal(map.Zones.Select(zone => zone.Id).Order(),
            graph.GetProperty("graph").GetProperty("nodes").EnumerateArray().Select(node => node.GetProperty("id").GetGuid()).Order());
        Assert.Equal(map.Routes.Select(route => route.Id).Order(),
            graph.GetProperty("graph").GetProperty("edges").EnumerateArray().Select(edge => edge.GetProperty("id").GetGuid()).Order());
        Assert.Equal(map.Gates[0].Id, graph.GetProperty("gateBindings")[0].GetProperty("resourceId").GetGuid());
    }

    [Fact]
    public async Task Navigation_limits_return_field_issues_without_changing_the_stored_map()
    {
        var map = MapFixture.Create();
        map.Zones[0].Name = new string('x', 161);
        using var created = await client.PostAsJsonAsync("/api/airports", map, MapFixture.Json);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var response = await client.GetAsync($"/api/airports/{map.Airport.Id}/navigation");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(error.GetProperty("issues").EnumerateArray(), issue =>
            issue.GetProperty("code").GetString() == "navigation_limit"
            && issue.GetProperty("objectId").GetGuid() == map.Zones[0].Id
            && issue.GetProperty("field").GetString() == "name");
        var snapshot = await client.GetFromJsonAsync<AirportMapSnapshot>(created.Headers.Location, MapFixture.Json);
        Assert.NotNull(snapshot);
        Assert.Equal(map.Zones[0].Name, snapshot.Map.Zones.Single(zone => zone.Id == map.Zones[0].Id).Name);
    }

    [Fact]
    public async Task Navigation_for_an_unknown_airport_returns_not_found()
    {
        using var response = await client.GetAsync($"/api/airports/{Guid.NewGuid()}/navigation");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("valid", HttpStatusCode.OK)]
    [InlineData("warning", HttpStatusCode.OK)]
    [InlineData("invalid", HttpStatusCode.UnprocessableEntity)]
    public async Task Validation_reports_issues_without_saving_the_map(string variant, HttpStatusCode expected)
    {
        var map = MapFixture.Create();
        if (variant == "warning") map.Zones[2].IsActive = false;
        if (variant == "invalid") map.Zones[0].Capacity = 0;

        using var response = await client.PostAsJsonAsync("/api/airports/validate", map, MapFixture.Json);
        Assert.Equal(expected, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var issues = body.GetProperty("issues").EnumerateArray().ToArray();
        if (variant == "valid") Assert.Empty(issues);
        else Assert.Contains(issues, issue => issue.GetProperty("code").GetString()
            == (variant == "warning" ? "inactive_zone" : "invalid_value"));
        if (expected == HttpStatusCode.OK)
        {
            Assert.Equal(1, body.GetProperty("schemaVersion").GetInt32());
            Assert.True(body.GetProperty("isNavigable").GetBoolean());
        }
        Assert.Empty((await client.GetFromJsonAsync<Airport[]>("/api/airports"))!);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"unknown\":true}")]
    [InlineData("{\"airport\":")]
    public async Task Validation_rejects_malformed_requests(string json)
    {
        using var body = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/api/airports/validate", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private sealed class AirportApiFactory(string path) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<AirportMapDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<AirportMapDbContext>>();
                services.AddDbContext<AirportMapDbContext>(options => options.UseSqlite($"Data Source={path};Pooling=False"));
            });
        }
    }
}
