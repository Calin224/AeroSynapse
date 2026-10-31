using MediatR;

namespace AeroSynapse.Application.Health;

public sealed record GetHealthQuery : IRequest<HealthResponse>;

public sealed record HealthResponse(string Status, DateTimeOffset TimestampUtc);

internal sealed class GetHealthQueryHandler(TimeProvider clock)
    : IRequestHandler<GetHealthQuery, HealthResponse>
{
    public Task<HealthResponse> Handle(GetHealthQuery request, CancellationToken cancellationToken)
        => Task.FromResult(new HealthResponse("Healthy", clock.GetUtcNow()));
}
