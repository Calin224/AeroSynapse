using AeroSynapse.Domain.Airports;
using MediatR;

namespace AeroSynapse.Application.Airports;

public sealed record CreateAirportMapCommand(AirportMap Map) : IRequest<AirportMapSnapshot>;
public sealed record GetAirportSnapshotQuery(Guid AirportId) : IRequest<AirportMapSnapshot?>;
public sealed record ListAirportsQuery : IRequest<IReadOnlyList<Airport>>;
internal sealed class ListAirportsHandler(IAirportMapRepository repository) : IRequestHandler<ListAirportsQuery, IReadOnlyList<Airport>>
{
    public Task<IReadOnlyList<Airport>> Handle(ListAirportsQuery request, CancellationToken cancellationToken) => repository.ListAsync(cancellationToken);
}

internal sealed class CreateAirportMapHandler(IAirportMapRepository repository)
    : IRequestHandler<CreateAirportMapCommand, AirportMapSnapshot>
{
    public async Task<AirportMapSnapshot> Handle(CreateAirportMapCommand request, CancellationToken cancellationToken)
    {
        var issues = AirportMapValidator.Validate(request.Map);
        if (issues.Any(issue => issue.Severity == MapIssueSeverity.Error))
            throw new AirportMapValidationException(issues);

        await repository.AddAsync(request.Map, cancellationToken);
        return new AirportMapSnapshot(request.Map, issues);
    }
}

internal sealed class GetAirportSnapshotHandler(IAirportMapRepository repository)
    : IRequestHandler<GetAirportSnapshotQuery, AirportMapSnapshot?>
{
    public async Task<AirportMapSnapshot?> Handle(GetAirportSnapshotQuery request, CancellationToken cancellationToken)
    {
        var map = await repository.GetAsync(request.AirportId, cancellationToken);
        return map is null ? null : new AirportMapSnapshot(map, AirportMapValidator.Validate(map));
    }
}
