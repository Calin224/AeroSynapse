using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AeroSynapse.Infrastructure.Audit;

public sealed record AuditIdentity(string ActorId, string Actor, string CorrelationId);

public static class AuditContext
{
    private static readonly AsyncLocal<AuditIdentity?> Slot = new();
    public static AuditIdentity Current => Slot.Value ?? new("system", "system", "");
    public static IDisposable Begin(AuditIdentity identity)
    {
        var previous = Slot.Value;
        Slot.Value = identity;
        return new Restore(() => Slot.Value = previous);
    }
    private sealed class Restore(Action restore) : IDisposable { public void Dispose() => restore(); }
}

// EF ExecuteUpdate/Delete and explicit SQL bypass SaveChanges interceptors. Set
// context on every command, including pooled connections and worker scopes.
public sealed class AuditCommandInterceptor : DbCommandInterceptor
{
    public static readonly AuditCommandInterceptor Instance = new();
    private static DbCommand ContextCommand(DbCommand command)
    {
        var metadata = command.Connection!.CreateCommand();
        metadata.Transaction = command.Transaction;
        metadata.CommandTimeout = command.CommandTimeout;
        metadata.CommandText = "SELECT set_config('aerosynapse.actor_id', @actor_id, false), set_config('aerosynapse.actor', @actor, false), set_config('aerosynapse.correlation', @correlation, false)";
        var identity = AuditContext.Current;
        foreach (var (name, value) in new[] { ("actor_id", identity.ActorId), ("actor", identity.Actor), ("correlation", identity.CorrelationId) })
        {
            var parameter = metadata.CreateParameter(); parameter.ParameterName = name; parameter.Value = value; metadata.Parameters.Add(parameter);
        }
        return metadata;
    }
    private static void Set(DbCommand command) { using var context = ContextCommand(command); context.ExecuteNonQuery(); }
    private static async Task SetAsync(DbCommand command, CancellationToken token) { await using var context = ContextCommand(command); await context.ExecuteNonQueryAsync(token); }
    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result) { Set(command); return result; }
    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken token = default) { await SetAsync(command, token); return result; }
    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData data, InterceptionResult<int> result) { Set(command); return result; }
    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<int> result, CancellationToken token = default) { await SetAsync(command, token); return result; }
    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData data, InterceptionResult<object> result) { Set(command); return result; }
    public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<object> result, CancellationToken token = default) { await SetAsync(command, token); return result; }
}
