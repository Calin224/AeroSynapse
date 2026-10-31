using AeroSynapse.Application.Airports;
using AeroSynapse.Domain.Airports;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AeroSynapse.Infrastructure.Airports;

internal sealed class AirportMapRepository(AirportMapDbContext db) : IAirportMapRepository
{
    public async Task<IReadOnlyList<Airport>> ListAsync(CancellationToken cancellationToken) =>
        await db.Airports.AsNoTracking().OrderBy(item => item.Name).ThenBy(item => item.Id).Take(100).ToArrayAsync(cancellationToken);
    public async Task AddAsync(AirportMap map, CancellationToken cancellationToken)
    {
        var issues = AirportMapValidator.Validate(map);
        if (issues.Any(issue => issue.Severity == MapIssueSeverity.Error))
            throw new AirportMapValidationException(issues);

        // SQLite reserves the write lock before checking IDs across entity tables.
        // Concurrent creates must observe the previous writer's committed IDs.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var ids = map.Elements().Select(item => item.Id).ToArray();
        var existingIds = db.Airports.Select(item => item.Id)
            .Concat(db.Terminals.Select(item => item.Id)).Concat(db.Floors.Select(item => item.Id))
            .Concat(db.Zones.Select(item => item.Id)).Concat(db.Routes.Select(item => item.Id))
            .Concat(db.Checkpoints.Select(item => item.Id)).Concat(db.Gates.Select(item => item.Id))
            .Concat(db.ResourcePools.Select(item => item.Id));
        if (await existingIds.AnyAsync(id => ids.Contains(id), cancellationToken))
            throw new AirportMapConflictException();

        db.Airports.Add(map.Airport);
        db.Terminals.AddRange(map.Terminals);
        db.Floors.AddRange(map.Floors);
        db.Zones.AddRange(map.Zones);
        db.Routes.AddRange(map.Routes);
        db.Checkpoints.AddRange(map.Checkpoints);
        db.Gates.AddRange(map.Gates);
        db.ResourcePools.AddRange(map.ResourcePools);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqliteException
            { SqliteExtendedErrorCode: 1555 or 2067 })
        {
            throw new AirportMapConflictException();
        }
    }

    public async Task<AirportMap?> GetAsync(Guid airportId, CancellationToken cancellationToken)
    {
        var airport = await db.Airports.AsNoTracking().SingleOrDefaultAsync(item => item.Id == airportId, cancellationToken);
        if (airport is null) return null;

        return new AirportMap
        {
            Airport = airport,
            Terminals = await db.Terminals.AsNoTracking().Where(item => item.AirportId == airportId).OrderBy(item => item.Id).ToListAsync(cancellationToken),
            Floors = await db.Floors.AsNoTracking().Where(item => item.AirportId == airportId).OrderBy(item => item.Id).ToListAsync(cancellationToken),
            Zones = await db.Zones.AsNoTracking().Where(item => item.AirportId == airportId).OrderBy(item => item.Id).ToListAsync(cancellationToken),
            Routes = await db.Routes.AsNoTracking().Where(item => item.AirportId == airportId).OrderBy(item => item.Id).ToListAsync(cancellationToken),
            Checkpoints = await db.Checkpoints.AsNoTracking().Where(item => item.AirportId == airportId).OrderBy(item => item.Id).ToListAsync(cancellationToken),
            Gates = await db.Gates.AsNoTracking().Where(item => item.AirportId == airportId).OrderBy(item => item.Id).ToListAsync(cancellationToken),
            ResourcePools = await db.ResourcePools.AsNoTracking().Where(item => item.AirportId == airportId).OrderBy(item => item.Id).ToListAsync(cancellationToken)
        };
    }
}
