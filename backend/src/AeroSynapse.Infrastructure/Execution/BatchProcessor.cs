using AeroSynapse.Application.Management;
using AeroSynapse.Application.Simulation;
using AeroSynapse.Domain.DigitalTwin;
using AeroSynapse.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace AeroSynapse.Infrastructure.Execution;

public sealed class BatchProcessor(TwinDbContext db, IServiceScopeFactory scopes, IIntelligenceClient client, ILogger<BatchProcessor> logger)
{
    public async Task<bool> ProcessNextAsync(CancellationToken token)
    {
        db.ChangeTracker.Clear();
        var attemptedCancellation = await CancelRemote(token);
        var ids = await db.Batches.AsNoTracking().Where(b => b.Status == "pending" || b.Status == "running")
            .OrderBy(b => b.CreatedAtUtc).ThenBy(b => b.Id).Select(b => b.Id).Take(32).ToArrayAsync(token);
        foreach (var id in ids)
        {
            await using var lease = new NpgsqlConnection(db.Database.GetConnectionString());
            await lease.OpenAsync(token);
            await using var acquire = new NpgsqlCommand("SELECT pg_try_advisory_lock(hashtextextended(@id, 26002))", lease);
            acquire.Parameters.AddWithValue("id", id.ToString());
            if (await acquire.ExecuteScalarAsync(token) is not true) continue;
            try { await Process(id, token); }
            finally
            {
                await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock(hashtextextended(@id, 26002))", lease);
                release.Parameters.AddWithValue("id", id.ToString()); await release.ExecuteScalarAsync(CancellationToken.None);
            }
            return true;
        }
        return attemptedCancellation;
    }

    private async Task Process(Guid id, CancellationToken token)
    {
        BatchReplication? replication; DateTime deadline;
        await using (var transaction = await db.Database.BeginTransactionAsync(token))
        {
            var batch = await Lock(id, token);
            if (BatchService.Terminal(batch.Status)) return;
            if (StopReason(batch) is { } stop) { await Stop(batch, stop, token); await transaction.CommitAsync(token); return; }
            if (batch.Status == "pending") { batch.Status = "running"; batch.StartedAtUtc = DateTime.UtcNow; batch.Revision++; }
            replication = await (from r in db.BatchReplications join v in db.BatchVariants on r.VariantId equals v.Id
                where r.BatchId == id && (r.Status == "pending" || r.Status == "running")
                orderby r.Status == "running" ? 0 : 1, r.SeedIndex, v.Position select r).FirstOrDefaultAsync(token);
            if (replication is null) { await Finish(batch, token); await transaction.CommitAsync(token); return; }
            replication.Status = "running"; deadline = batch.DeadlineAtUtc;
            await db.SaveChangesAsync(token); await transaction.CommitAsync(token);
        }
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["BatchId"] = id, ["RunId"] = replication.RunId, ["Seed"] = replication.Seed });
        logger.LogInformation("Batch replication running batch={BatchId} run={RunId} seed={Seed}", id, replication.RunId, replication.Seed);
        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var control = new ExecutionControl(Math.Clamp((int)Math.Ceiling((deadline - DateTime.UtcNow).TotalMilliseconds), 1, 3_600_000));
        var work = Execute(replication.RunId, control, executionCancellation.Token);
        try
        {
            while (!work.IsCompleted)
            {
                await Task.WhenAny(work, Task.Delay(200, token));
                token.ThrowIfCancellationRequested();
                var current = await db.Batches.AsNoTracking().SingleAsync(b => b.Id == id, token);
                if (StopReason(current) is not null) { executionCancellation.Cancel(); break; }
            }
            try { await work.WaitAsync(TimeSpan.FromSeconds(2), token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            catch (TimeoutException) { Observe(work); }
            catch (Exception error) when (!token.IsCancellationRequested) { logger.LogError(error, "Batch replication interrupted batch={BatchId} run={RunId}", id, replication.RunId); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            executionCancellation.Cancel(); Observe(work); throw; // Durable receipt is reconciled on restart.
        }
        db.ChangeTracker.Clear();
        await using (var transaction = await db.Database.BeginTransactionAsync(token))
        {
            var batch = await Lock(id, token);
            if (StopReason(batch) is { } stop) await Stop(batch, stop, token);
            else
            {
                var replica = await db.BatchReplications.SingleAsync(r => r.Id == replication.Id, token);
                var run = await db.Runs.AsNoTracking().SingleAsync(r => r.Id == replica.RunId, token);
                if (run.Status is not ("completed" or "failed"))
                { await FailRun(run.Id, "batch_execution_failed: The replication worker ended without a terminal result.", token); replica.Status = "failed"; }
                else replica.Status = ReplicationStatus(run);
                await db.SaveChangesAsync(token); await Finish(batch, token);
            }
            await transaction.CommitAsync(token);
        }
    }

    private async Task Execute(Guid runId, ExecutionControl control, CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IRunExecutionService>().ExecuteBatchRunAsync(runId, control, token);
    }
    private static void Observe(Task work) => _ = work.ContinueWith(task => { _ = task.Exception; },
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    private Task<SimulationBatch> Lock(Guid id, CancellationToken token) => db.Batches
        .FromSqlInterpolated($"SELECT * FROM twin.simulation_batches WHERE \"Id\" = {id} FOR UPDATE").SingleAsync(token);
    private static string? StopReason(SimulationBatch batch) => batch.CancellationRequestedAtUtc.HasValue ? "cancelled"
        : DateTime.UtcNow >= batch.DeadlineAtUtc ? "timed_out" : null;
    private static string ReplicationStatus(SimulationRun run) => run.Status == "completed" ? "completed"
        : run.FailureReason?.StartsWith("simulation_cancelled:", StringComparison.Ordinal) == true || run.FailureReason?.StartsWith("batch_cancelled:", StringComparison.Ordinal) == true ? "cancelled"
        : run.FailureReason?.StartsWith("simulation_timeout:", StringComparison.Ordinal) == true || run.FailureReason?.StartsWith("batch_timeout:", StringComparison.Ordinal) == true ? "timed_out" : "failed";

    private async Task Stop(SimulationBatch batch, string status, CancellationToken token)
    {
        var reason = status == "cancelled" ? "batch_cancelled: Batch cancellation was requested."
            : "batch_timeout: The batch wall-clock deadline expired.";
        var replicas = await db.BatchReplications.Where(r => r.BatchId == batch.Id && r.Status != "completed")
            .OrderBy(r => r.RunId).ToArrayAsync(token);
        foreach (var replica in replicas)
        {
            var run = await db.Runs.AsNoTracking().SingleAsync(r => r.Id == replica.RunId, token);
            if (run.Status is "completed" or "failed")
            {
                // An earlier transport/contract failure may have stopped .NET waiting while
                // the controlled Python child still computes. Cancellation covers it too.
                if (run.Status == "failed" && run.LaunchedAtUtc.HasValue)
                { replica.RemoteCancellationPending = true; replica.NextCancellationAttemptAtUtc = DateTime.UtcNow; }
                replica.Status = ReplicationStatus(run); continue;
            }
            replica.RemoteCancellationPending = replica.Status == "running";
            replica.NextCancellationAttemptAtUtc = replica.RemoteCancellationPending ? DateTime.UtcNow : null;
            await FailRun(run.Id, run.Status == "planned" ? reason + " Computation was not started." : reason, token);
            replica.Status = status;
        }
        batch.Status = status; batch.FailureReason = reason; batch.FinishedAtUtc = DateTime.UtcNow; batch.Revision++;
        await db.SaveChangesAsync(token);
        logger.LogInformation("Batch stopped batch={BatchId} status={Status}", batch.Id, status);
    }
    private async Task FailRun(Guid runId, string reason, CancellationToken token)
    {
        // Existing run lifecycle has no cancelled status. Preserve its terminal failed/error contract,
        // while the replication explicitly distinguishes cancellation and timeout from model failures.
        await db.Runs.Where(r => r.Id == runId && r.Status == "planned").ExecuteUpdateAsync(setters => setters
            .SetProperty(r => r.Status, "running").SetProperty(r => r.LaunchedAtUtc, DateTime.UtcNow).SetProperty(r => r.Revision, r => r.Revision + 1), token);
        await db.Runs.Where(r => r.Id == runId && r.Status == "running").ExecuteUpdateAsync(setters => setters
            .SetProperty(r => r.Status, "failed").SetProperty(r => r.FailureReason, reason).SetProperty(r => r.FinishedAtUtc, DateTime.UtcNow)
            .SetProperty(r => r.Revision, r => r.Revision + 1), token);
    }
    private async Task Finish(SimulationBatch batch, CancellationToken token)
    {
        var states = await db.BatchReplications.Where(r => r.BatchId == batch.Id).Select(r => r.Status).ToArrayAsync(token);
        if (states.Any(s => s is "pending" or "running")) return;
        batch.Status = states.All(s => s == "completed") ? "completed" : "completed_with_failures";
        batch.FinishedAtUtc = DateTime.UtcNow; batch.Revision++; await db.SaveChangesAsync(token);
    }
    private async Task<bool> CancelRemote(CancellationToken token)
    {
        var now = DateTime.UtcNow;
        var replica = await db.BatchReplications.AsNoTracking().Where(r => r.RemoteCancellationPending && r.NextCancellationAttemptAtUtc <= now)
            .OrderBy(r => r.NextCancellationAttemptAtUtc).FirstOrDefaultAsync(token);
        if (replica is null) return false;
        try
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token); attempt.CancelAfter(TimeSpan.FromSeconds(3));
            await client.CancelAsync(replica.RunId, replica.RunId.ToString("N"), attempt.Token);
            await db.BatchReplications.Where(r => r.Id == replica.Id).ExecuteUpdateAsync(setters => setters
                .SetProperty(r => r.RemoteCancellationPending, false).SetProperty(r => r.NextCancellationAttemptAtUtc, (DateTime?)null), token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            logger.LogWarning(error, "Remote cancellation will retry batch={BatchId} run={RunId}", replica.BatchId, replica.RunId);
            await db.BatchReplications.Where(r => r.Id == replica.Id).ExecuteUpdateAsync(setters => setters
                .SetProperty(r => r.NextCancellationAttemptAtUtc, DateTime.UtcNow.AddSeconds(2)), token);
        }
        return true;
    }
}
