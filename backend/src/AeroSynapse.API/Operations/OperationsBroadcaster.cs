using AeroSynapse.Application.Operations;
using AeroSynapse.Infrastructure.Operations;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using AeroSynapse.API.Identity;
using AeroSynapse.Infrastructure.Storage;

namespace AeroSynapse.API.Operations;

internal sealed class OperationsBroadcaster(IServiceScopeFactory scopes, IHubContext<OperationsHub> hub,
    ScenarioSubscriptions subscriptions, IOptions<OperationsOptions> options, ILogger<OperationsBroadcaster> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            foreach (var subscription in subscriptions.Active())
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var access = scope.ServiceProvider.GetRequiredService<WorkspaceIdentity>();
                    var database = scope.ServiceProvider.GetRequiredService<TwinDbContext>();
                    var allowed = new List<ScenarioSubscriptions.Member>();
                    foreach (var member in subscriptions.Members(subscription.ScenarioId))
                    {
                        if (await access.CanReadScenarioAsync(member.Principal, subscription.ScenarioId, database, token)) allowed.Add(member);
                        else
                        {
                            subscriptions.Remove(subscription.ScenarioId, member.ConnectionId);
                            member.Abort();
                        }
                    }
                    if (allowed.Count == 0) continue;
                    var messages = await scope.ServiceProvider.GetRequiredService<IOperationsReader>()
                        .EventsAsync(subscription.ScenarioId, subscription.Sequence, options.Value.MaxEventsPerBatch, token);
                    foreach (var message in messages)
                    {
                        foreach (var member in allowed)
                            await hub.Clients.Client(member.ConnectionId).SendAsync(message.Type, message, token);
                        subscription.Sequence = message.Sequence;
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (Exception error) { logger.LogWarning(error, "Operations stream will recover scenario {ScenarioId} through its persistent cursor.", subscription.ScenarioId); }
            }
            try { await Task.Delay(options.Value.BroadcastIntervalMilliseconds, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        }
    }
}
