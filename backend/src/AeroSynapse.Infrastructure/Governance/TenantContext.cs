namespace AeroSynapse.Infrastructure.Governance;

// Authority comes from a validated Identity session or a server-owned worker scope.
// Tenant IDs from headers, JSON, prompts and resource IDs never enter this context.
public static class TenantContext
{
    public const string Default = "demo";
    public const string Claim = "aerosynapse_tenant";
    private static readonly AsyncLocal<string?> Slot = new();
    public static string Id => Slot.Value ?? Default;
    public static IDisposable Begin(string id)
    {
        var previous = Slot.Value;
        Slot.Value = id;
        return new Restore(() => Slot.Value = previous);
    }
    private sealed class Restore(Action action) : IDisposable { public void Dispose() => action(); }
}
