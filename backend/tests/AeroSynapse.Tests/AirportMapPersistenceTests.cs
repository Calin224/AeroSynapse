using System.Text.Json;
using AeroSynapse.Application;
using AeroSynapse.Application.Airports;
using AeroSynapse.Domain.Airports;
using AeroSynapse.Infrastructure;
using AeroSynapse.Infrastructure.Airports;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AeroSynapse.Tests;

public sealed class AirportMapPersistenceTests : IAsyncLifetime
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"aerosynapse-{Guid.NewGuid():N}.db");
    private ServiceProvider provider = null!;

    private ServiceProvider BuildProvider() => new ServiceCollection().AddApplication()
        .AddInfrastructure($"Data Source={path};Pooling=False")
        .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

    public async Task InitializeAsync()
    {
        provider = BuildProvider();
        using var scope = provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AirportMapDbContext>().Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await provider.DisposeAsync();
        foreach (var suffix in new[] { "", "-shm", "-wal" }) File.Delete(path + suffix);
    }

    private async Task Save(AirportMap map)
    {
        using var scope = provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ISender>().Send(new CreateAirportMapCommand(map));
    }

    [Fact]
    public async Task Snapshot_preserves_all_entities_and_ids_after_a_service_restart()
    {
        var map = MapFixture.Create();
        await Save(map);
        await provider.DisposeAsync();
        provider = BuildProvider();
        using var scope = provider.CreateScope();
        var snapshot = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new GetAirportSnapshotQuery(map.Airport.Id));
        Assert.NotNull(snapshot);
        Assert.True(snapshot.IsNavigable);
        Assert.Empty(snapshot.Issues);
        var json = JsonSerializer.Serialize(snapshot, MapFixture.Json);
        var copy = JsonSerializer.Deserialize<AirportMapSnapshot>(json, MapFixture.Json)!;
        Assert.Equal(1, copy.SchemaVersion);
        Assert.Equal(map.Elements().Select(item => item.Id).Order(), copy.Map.Elements().Select(item => item.Id).Order());
        Assert.Equal(map.Routes[0].TravelDurationSeconds, copy.Map.Routes.Single(item => item.Id == map.Routes[0].Id).TravelDurationSeconds);
        Assert.Equal(map.Zones[2].X, copy.Map.Zones.Single(item => item.Id == map.Zones[2].Id).X);
        Assert.Equal(map.Checkpoints[0].ResourcePoolId, Assert.Single(copy.Map.Checkpoints).ResourcePoolId);
    }

    [Fact]
    public async Task Concurrent_creates_with_cross_type_id_collisions_store_only_one_complete_map()
    {
        var first = MapFixture.Create();
        var second = MapFixture.Create();
        second.Gates[0].Id = first.ResourcePools[0].Id;
        using var start = new Barrier(2);

        Task<bool> Create(AirportMap map) => Task.Run(async () =>
        {
            Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(10)));
            try
            {
                await Save(map);
                return true;
            }
            catch (AirportMapConflictException)
            {
                return false;
            }
        });

        var results = await Task.WhenAll(Create(first), Create(second));
        Assert.Single(results, saved => saved);
        Assert.Single(results, saved => !saved);
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AirportMapDbContext>();
        Assert.Equal(1, await db.Airports.CountAsync());
        Assert.Equal(1, await db.Terminals.CountAsync());
        Assert.Equal(1, await db.Floors.CountAsync());
        Assert.Equal(3, await db.Zones.CountAsync());
        Assert.Equal(2, await db.Routes.CountAsync());
        Assert.Equal(1, await db.Checkpoints.CountAsync());
        Assert.Equal(1, await db.Gates.CountAsync());
        Assert.Equal(1, await db.ResourcePools.CountAsync());
    }

    [Theory]
    [InlineData("airport")]
    [InlineData("terminal")]
    [InlineData("capacity")]
    [InlineData("rate")]
    [InlineData("coordinate")]
    public async Task Database_constraints_reject_invalid_writes_that_bypass_application_validation(string mutation)
    {
        var map = MapFixture.Create();
        await Save(map);
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AirportMapDbContext>();
        switch (mutation)
        {
            case "airport":
                var otherAirport = new Airport { Id = Guid.NewGuid(), Name = "Another airport" };
                db.Airports.Add(otherAirport);
                await db.SaveChangesAsync();
                db.Floors.Add(new Floor { Id = Guid.NewGuid(), AirportId = otherAirport.Id, TerminalId = map.Terminals[0].Id, Name = "Wrong airport", Level = 1 });
                break;
            case "terminal":
                var otherTerminal = new Terminal { Id = Guid.NewGuid(), AirportId = map.Airport.Id, Name = "Another terminal" };
                db.Terminals.Add(otherTerminal);
                await db.SaveChangesAsync();
                db.Routes.Add(new Route { Id = Guid.NewGuid(), AirportId = map.Airport.Id, TerminalId = otherTerminal.Id,
                    FromZoneId = map.Zones[0].Id, ToZoneId = map.Zones[1].Id, Name = "Wrong terminal", TravelDurationSeconds = 10 });
                break;
            case "capacity": (await db.Zones.FirstAsync()).Capacity = 0; break;
            case "rate": (await db.Checkpoints.FirstAsync()).ProcessingRatePerMinute = double.PositiveInfinity; break;
            case "coordinate": (await db.Zones.FirstAsync()).X = double.PositiveInfinity; break;
        }
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
