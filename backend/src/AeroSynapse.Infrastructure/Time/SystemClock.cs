using AeroSynapse.Application.Abstractions;

namespace AeroSynapse.Infrastructure.Time;

internal sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
