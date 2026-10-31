using AeroSynapse.Domain.Airports;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AeroSynapse.Infrastructure.Airports;

public sealed class AirportMapDbContext(DbContextOptions<AirportMapDbContext> options) : DbContext(options)
{
    public DbSet<Airport> Airports => Set<Airport>();
    public DbSet<Terminal> Terminals => Set<Terminal>();
    public DbSet<Floor> Floors => Set<Floor>();
    public DbSet<Zone> Zones => Set<Zone>();
    public DbSet<Route> Routes => Set<Route>();
    public DbSet<Checkpoint> Checkpoints => Set<Checkpoint>();
    public DbSet<Gate> Gates => Set<Gate>();
    public DbSet<ResourcePool> ResourcePools => Set<ResourcePool>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        Configure<Airport>(model);
        Configure<Terminal>(model).HasOne<Airport>().WithMany()
            .HasForeignKey(item => item.AirportId).OnDelete(DeleteBehavior.Restrict);
        ConfigureTerminalElement<Floor>(model);
        ConfigureTerminalElement<Zone>(model);
        ConfigureTerminalElement<Route>(model);
        ConfigureTerminalElement<ResourcePool>(model);
        ConfigureTerminalElement<Checkpoint>(model);
        ConfigureTerminalElement<Gate>(model);

        model.Entity<Floor>().HasIndex(item => new { item.AirportId, item.TerminalId, item.Level }).IsUnique();
        model.Entity<Floor>().ToTable(table => table.HasCheckConstraint("CK_Floor_Elevation", Finite("ElevationMeters")));
        model.Entity<Zone>().HasOne<Floor>().WithMany()
            .HasForeignKey(item => new { item.AirportId, item.TerminalId, item.FloorId })
            .HasPrincipalKey(item => new { item.AirportId, item.TerminalId, item.Id })
            .OnDelete(DeleteBehavior.Restrict);
        model.Entity<Zone>().Property(item => item.Type).HasConversion<string>();
        model.Entity<Zone>().ToTable(table =>
        {
            table.HasCheckConstraint("CK_Zone_Capacity", "Capacity > 0");
            table.HasCheckConstraint("CK_Zone_Coordinates", $"{Finite("X")} AND {Finite("Y")} AND {Finite("Z")}");
            table.HasCheckConstraint("CK_Zone_Type", "Type IN ('Entrance','Walkway','CheckIn','Security','PassportControl','Gate','Lounge','BaggageClaim','Stairs','Elevator')");
        });

        model.Entity<Route>().HasOne<Zone>().WithMany()
            .HasForeignKey(item => new { item.AirportId, item.TerminalId, item.FromZoneId })
            .HasPrincipalKey(item => new { item.AirportId, item.TerminalId, item.Id })
            .OnDelete(DeleteBehavior.Restrict);
        model.Entity<Route>().HasOne<Zone>().WithMany()
            .HasForeignKey(item => new { item.AirportId, item.TerminalId, item.ToZoneId })
            .HasPrincipalKey(item => new { item.AirportId, item.TerminalId, item.Id })
            .OnDelete(DeleteBehavior.Restrict);
        model.Entity<Route>().ToTable(table =>
        {
            table.HasCheckConstraint("CK_Route_Duration", $"TravelDurationSeconds > 0 AND {Finite("TravelDurationSeconds")}");
            table.HasCheckConstraint("CK_Route_Endpoints", "FromZoneId <> ToZoneId");
        });

        ConfigureZoneReference<ResourcePool>(model);
        ConfigureZoneReference<Checkpoint>(model);
        ConfigureZoneReference<Gate>(model);
        model.Entity<ResourcePool>().Property(item => item.Type).HasConversion<string>();
        model.Entity<ResourcePool>().ToTable(table =>
        {
            table.HasCheckConstraint("CK_ResourcePool_Capacity", "Capacity > 0");
            table.HasCheckConstraint("CK_ResourcePool_Type", "Type IN ('Staff','SecurityLane','CheckInDesk','Equipment')");
        });
        model.Entity<Checkpoint>().HasOne<ResourcePool>().WithMany()
            .HasForeignKey(item => new { item.AirportId, item.TerminalId, item.ResourcePoolId })
            .HasPrincipalKey(item => new { item.AirportId, item.TerminalId, item.Id })
            .OnDelete(DeleteBehavior.Restrict);
        model.Entity<Checkpoint>().ToTable(table =>
        {
            table.HasCheckConstraint("CK_Checkpoint_Capacity", "Capacity > 0");
            table.HasCheckConstraint("CK_Checkpoint_Rate", $"ProcessingRatePerMinute > 0 AND {Finite("ProcessingRatePerMinute")}");
        });
        model.Entity<Gate>().ToTable(table =>
        {
            table.HasCheckConstraint("CK_Gate_Capacity", "Capacity > 0");
            table.HasCheckConstraint("CK_Gate_Rate", $"BoardingRatePerMinute > 0 AND {Finite("BoardingRatePerMinute")}");
        });
    }

    private static EntityTypeBuilder<T> Configure<T>(ModelBuilder model) where T : class, IMapElement
    {
        var entity = model.Entity<T>();
        entity.ToTable(typeof(T).Name, table =>
        {
            table.HasCheckConstraint($"CK_{typeof(T).Name}_Id", "Id <> '00000000-0000-0000-0000-000000000000'");
            table.HasCheckConstraint($"CK_{typeof(T).Name}_Name", "length(trim(Name)) BETWEEN 1 AND 200");
        });
        entity.HasKey(nameof(IMapElement.Id));
        entity.Property<Guid>(nameof(IMapElement.Id)).ValueGeneratedNever();
        entity.Property<string>(nameof(IMapElement.Name)).HasMaxLength(200).IsRequired();
        return entity;
    }

    private static void ConfigureTerminalElement<T>(ModelBuilder model) where T : class, ITerminalElement
    {
        Configure<T>(model).HasOne<Terminal>().WithMany()
            .HasForeignKey(nameof(IAirportElement.AirportId), nameof(ITerminalElement.TerminalId))
            .HasPrincipalKey(item => new { item.AirportId, item.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureZoneReference<T>(ModelBuilder model) where T : class, ITerminalElement
    {
        model.Entity<T>().HasOne<Zone>().WithMany()
            .HasForeignKey(nameof(IAirportElement.AirportId), nameof(ITerminalElement.TerminalId), "ZoneId")
            .HasPrincipalKey(item => new { item.AirportId, item.TerminalId, item.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static string Finite(string column) => $"abs({column}) <= 1.7976931348623157e308";
}
