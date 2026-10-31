namespace AeroSynapse.Infrastructure.Operations;

public sealed class OperationsOptions
{
    public bool Enabled { get; set; } = true;
    public int BroadcastIntervalMilliseconds { get; set; } = 500;
    public int MaxEventsPerBatch { get; set; } = 32;
    public int EnginePollMilliseconds { get; set; } = 1000;
}
