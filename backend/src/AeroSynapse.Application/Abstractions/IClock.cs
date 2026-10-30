namespace AeroSynapse.Application.Abstractions;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
