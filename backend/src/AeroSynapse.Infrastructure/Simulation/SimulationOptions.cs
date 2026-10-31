namespace AeroSynapse.Infrastructure.Simulation;

public sealed class SimulationOptions
{
    public string BaseUrl { get; set; } = "http://localhost:8000/";
    public int TimeoutSeconds { get; set; } = 10;
    public int ReconciliationSeconds { get; set; } = 120;
    public int PollSeconds { get; set; } = 2;
    public int SafeRetryCount { get; set; } = 2;
}
