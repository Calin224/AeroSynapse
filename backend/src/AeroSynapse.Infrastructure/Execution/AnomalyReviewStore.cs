using System.Text.Json;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Management;
using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.DigitalTwin;
using AeroSynapse.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AeroSynapse.Infrastructure.Execution;

// Demo decisions: append-only rows, idempotent by command ID; nothing leaves the database.
public sealed class AnomalyReviewStore(TwinDbContext db) : IAnomalyReviewStore
{
    public async Task<IReadOnlyList<AnomalyReviewDetails>> ListAsync(Guid runId, CancellationToken token) =>
        (await db.AnomalyReviews.AsNoTracking().Where(item => item.RunId == runId)
            .OrderBy(item => item.RecordedAtUtc).ThenBy(item => item.Id).ToArrayAsync(token)).Select(Details).ToArray();

    public async Task<AnomalyReviewDetails> RecordAsync(AnomalyReviewRecord record, CancellationToken token)
    {
        var existing = await db.AnomalyReviews.AsNoTracking().SingleOrDefaultAsync(item => item.CommandId == record.CommandId, token);
        if (existing is not null) return Same(existing, record) ? Details(existing) : throw Reused();
        var review = new AnomalyReview { Id = Guid.NewGuid(), SceneId = record.SceneId, IsSynthetic = true, RunId = record.RunId,
            BatchId = record.BatchId, SettingsKey = record.SettingsKey, SettingsJson = JsonSerializer.Serialize(record.Settings, ContractJson.Options),
            DetectorVersion = record.DetectorVersion, CheckpointId = record.CheckpointId, OnsetMinute = record.OnsetMinute,
            Decision = record.Decision, CommandId = record.CommandId, Actor = record.Actor, Reason = record.Reason,
            RecordedAtUtc = DateTime.UtcNow, EpisodeJson = JsonSerializer.Serialize(record.Episode, ContractJson.Options) };
        db.AnomalyReviews.Add(review);
        try { await db.SaveChangesAsync(token); }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A concurrent retry with the same command won the insert.
            db.ChangeTracker.Clear();
            var winner = await db.AnomalyReviews.AsNoTracking().SingleAsync(item => item.CommandId == record.CommandId, token);
            return Same(winner, record) ? Details(winner) : throw Reused();
        }
        return Details(review);
    }

    private static bool Same(AnomalyReview item, AnomalyReviewRecord record) => item.RunId == record.RunId && item.BatchId == record.BatchId
        && item.SettingsKey == record.SettingsKey && item.CheckpointId == record.CheckpointId && item.OnsetMinute == record.OnsetMinute
        && item.Decision == record.Decision && item.Actor == record.Actor && item.Reason == record.Reason;

    private static ScenarioConflictException Reused() => new("The command ID was already used for a different decision.");

    private static AnomalyReviewDetails Details(AnomalyReview item) => new(item.Id, item.RunId, item.BatchId, item.SettingsKey,
        JsonSerializer.Deserialize<AnomalySettings>(item.SettingsJson, ContractJson.Options)!, item.DetectorVersion, item.CheckpointId,
        item.OnsetMinute, item.Decision, item.Actor, item.Reason, item.RecordedAtUtc);
}
