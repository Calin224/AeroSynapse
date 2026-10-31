using AeroSynapse.Domain.Airports;

namespace AeroSynapse.Application.Airports;

public interface IAirportMapRepository
{
    Task AddAsync(AirportMap map, CancellationToken cancellationToken);
    Task<AirportMap?> GetAsync(Guid airportId, CancellationToken cancellationToken);
    Task<IReadOnlyList<Airport>> ListAsync(CancellationToken cancellationToken);
}

public sealed class AirportMapConflictException()
    : Exception("One or more map IDs are already in use.");
