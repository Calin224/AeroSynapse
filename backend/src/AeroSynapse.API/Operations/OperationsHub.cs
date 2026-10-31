using AeroSynapse.Application.Operations;
using AeroSynapse.Application.Storage;
using Microsoft.AspNetCore.SignalR;
using AeroSynapse.API.Identity;
using AeroSynapse.Infrastructure.Storage;
using System.Security.Claims;
using AeroSynapse.Infrastructure.Governance;

namespace AeroSynapse.API.Operations;

public sealed class OperationsHub(ScenarioSubscriptions subscriptions, IServiceScopeFactory scopes) : Hub
{
    internal static string Group(Guid scenarioId) => $"tenant:{TenantContext.Id}:scenario:{scenarioId:N}";

    public async Task<object> SubscribeScenario(Guid scenarioId)
    {
        using var tenant = TenantContext.Begin(Context.User?.FindFirstValue(TenantContext.Claim) ?? "unavailable");
        await using var scope = scopes.CreateAsyncScope();
        var identity = scope.ServiceProvider.GetRequiredService<WorkspaceIdentity>();
        var database = scope.ServiceProvider.GetRequiredService<TwinDbContext>();
        var reader = scope.ServiceProvider.GetRequiredService<IOperationsReader>();
        if (scenarioId == Guid.Empty) throw new HubException("A scenario ID is required.");
        if (!await identity.CanReadScenarioAsync(Context.User, scenarioId, database, Context.ConnectionAborted))
            throw new HubException("scenario_access_denied: Session or scenario access is unavailable.");
        if (Context.Items.TryGetValue("lastSwitch", out var timestamp) && timestamp is DateTime last && DateTime.UtcNow - last < TimeSpan.FromMilliseconds(250))
            throw new HubException("Scenario changes are limited to four per second.");
        long sequence;
        try { sequence = await reader.CursorAsync(scenarioId, Context.ConnectionAborted); }
        catch (ScenarioNotFoundException) { throw new HubException("Scenario was not found."); }
        await UnsubscribeScenario();
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(scenarioId), Context.ConnectionAborted);
        subscriptions.Add(scenarioId, Context.ConnectionId, sequence, Context.User!, Context.Abort);
        Context.Items["scenario"] = scenarioId;
        Context.Items["lastSwitch"] = DateTime.UtcNow;
        return new { scenarioId, sequence };
    }

    public async Task UnsubscribeScenario()
    {
        using var tenant = TenantContext.Begin(Context.User?.FindFirstValue(TenantContext.Claim) ?? "unavailable");
        if (Context.Items.Remove("scenario", out var previous) && previous is Guid scenarioId)
        {
            subscriptions.Remove(scenarioId, Context.ConnectionId);
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(scenarioId));
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        using var tenant = TenantContext.Begin(Context.User?.FindFirstValue(TenantContext.Claim) ?? "unavailable");
        if (Context.Items.TryGetValue("scenario", out var previous) && previous is Guid scenarioId)
            subscriptions.Remove(scenarioId, Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}

public sealed class ScenarioSubscriptions
{
    public sealed class Subscription(Guid scenarioId, long sequence)
    {
        public Guid ScenarioId { get; } = scenarioId;
        public long Sequence { get; set; } = sequence;
        public Dictionary<string, Member> Connections { get; } = [];
    }
    public sealed record Member(string ConnectionId, ClaimsPrincipal Principal, Action Abort);
    private readonly object gate = new();
    private readonly Dictionary<(string TenantId, Guid ScenarioId), Subscription> scenarios = [];
    public void Add(Guid scenarioId, string connectionId, long sequence, ClaimsPrincipal principal, Action abort)
    {
        lock (gate)
        {
            var key = (TenantContext.Id, scenarioId);
            if (!scenarios.TryGetValue(key, out var subscription)) scenarios[key] = subscription = new(scenarioId, sequence);
            subscription.Connections[connectionId] = new(connectionId, principal, abort);
        }
    }
    public void Remove(Guid scenarioId, string connectionId)
    {
        lock (gate)
            if (scenarios.TryGetValue((TenantContext.Id, scenarioId), out var subscription) && subscription.Connections.Remove(connectionId) && subscription.Connections.Count == 0)
                scenarios.Remove((TenantContext.Id, scenarioId));
    }
    public Subscription[] Active() { lock (gate) return scenarios.Where(pair => pair.Key.TenantId == TenantContext.Id).Select(pair => pair.Value).ToArray(); }
    public Member[] Members(Guid scenarioId)
    {
        lock (gate) return scenarios.TryGetValue((TenantContext.Id, scenarioId), out var subscription) ? subscription.Connections.Values.ToArray() : [];
    }
}
