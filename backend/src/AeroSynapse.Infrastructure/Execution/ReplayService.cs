using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Management;
using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.DigitalTwin;
using AeroSynapse.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace AeroSynapse.Infrastructure.Execution;

public sealed class ReplayService(TwinDbContext db) : IReplayService
{
    public const string BookmarkType = "replay.bookmark.v1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed record BookmarkPayload(string ContractVersion, double Minute, string Label, Guid? ZoneId, string Note, int Revision);

    public async Task<ReplayEnvelope> GetAsync(Guid runId, CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        var (run, captured) = await Require(runId, token);
        var checkpoints = captured.Layout.Layout.Checkpoints.Select(c => c.Id).ToHashSet();
        var desks = captured.Layout.Layout.CheckInAreas?.Select(c => c.Id).ToHashSet() ?? [];
        var rows = await db.Snapshots.AsNoTracking()
            .Where(s => s.RunId == runId && (s.ZoneType == "checkpoint" || s.ZoneType == CheckInContractValidation.ZoneType))
            .OrderBy(s => s.RecordedAtUtc).ThenBy(s => s.ZoneId).ThenBy(s => s.Id).Take(KpiConventions.MaxSnapshots + 1).ToArrayAsync(token);
        if (rows.Length > KpiConventions.MaxSnapshots) throw new ScenarioConflictException("Replay exceeds the 50000 captured sample limit.");
        var samples = new List<ReplaySample>(); var keys = new HashSet<(Guid, double)>(); var hasKpis = false;
        foreach (var row in rows)
        {
            try
            {
                using var document = JsonDocument.Parse(row.PayloadJson); var payload = document.RootElement;
                var desk = row.ZoneType == CheckInContractValidation.ZoneType;
                var checkIn = desk ? ContractJson.Read<CheckInSnapshot>(payload, "CheckInSnapshot") : null;
                KpiSnapshot? kpi = !desk && payload.TryGetProperty("kpiVersion", out _) ? ContractJson.Read<KpiSnapshot>(payload, "KpiSnapshot") : null;
                var snapshot = !desk && kpi is null ? ContractJson.Read<ZoneSnapshot>(payload, "ZoneSnapshot") : null;
                var minute = checkIn?.Minute ?? kpi?.ElapsedMinutes ?? snapshot!.ElapsedMinutes;
                var zoneId = checkIn?.ZoneId ?? kpi?.ZoneId ?? snapshot!.ZoneId;
                if (zoneId != row.ZoneId || !(desk ? desks : checkpoints).Contains(zoneId) || kpi is not null && kpi.RunId != runId
                    || !double.IsFinite(minute) || minute < 0 || minute > captured.Scenario.Inputs.DurationMinutes
                    || Math.Abs((row.RecordedAtUtc - captured.Scenario.Inputs.StartsAtUtc.AddMinutes(minute)).TotalMilliseconds) > 1
                    || !keys.Add((zoneId, minute))) throw new JsonException();
                samples.Add(new(zoneId, minute, row.RecordedAtUtc, checkIn?.QueuedPassengers ?? kpi?.QueuedPassengers ?? snapshot!.QueuedPassengers,
                    checkIn?.ProcessedPassengers ?? kpi?.ProcessedPassengers ?? snapshot!.ProcessedPassengers, kpi, checkIn));
                hasKpis |= kpi is not null;
            }
            catch (Exception error) when (error is JsonException or ContractValidationException or ArgumentException)
            { throw new ScenarioConflictException("Captured replay samples are invalid or do not belong to this run."); }
        }
        var correlation = await db.SimulationCommands.Where(c => c.RunId == runId).Select(c => c.CorrelationId).SingleOrDefaultAsync(token);
        var data = new ReplayData(new(run.Id, run.SceneId, run.ScenarioId!.Value, run.ScenarioRevision!.Value, run.Seed,
            run.Status, run.ResultsValid, run.IsSynthetic, RunComparisonService.Provenance(run, correlation)), captured,
            samples.OrderBy(s => s.Minute).ThenBy(s => s.ZoneId).ToArray(), hasKpis ? captured.SnapshotIntervalMinutes ?? 1 : null);
        var json = JsonSerializer.Serialize(data, Json);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        return new("1.0", runId, hash, json, await ReadBookmarks(runId, token));
    }

    public async Task<IReadOnlyList<ReplayBookmark>> BookmarksAsync(Guid runId, CancellationToken token)
    { await Require(runId, token); return await ReadBookmarks(runId, token); }

    public async Task<ReplayBookmark> WriteBookmarkAsync(Guid runId, Guid bookmarkId, BookmarkWriteRequest request, CancellationToken token)
    {
        if (bookmarkId == Guid.Empty) throw Invalid("id", "A nonempty bookmark UUID is required.");
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await Lock(runId, token);
        var (run, captured) = await Require(runId, token);
        Validate(request, captured);
        var stored = await db.Events.SingleOrDefaultAsync(e => e.Id == bookmarkId, token);
        if (stored is not null && (stored.SimulationRunId != runId || stored.Type != BookmarkType))
            throw new ScenarioConflictException("This bookmark identifier belongs to another event or run.");
        var previous = stored is null ? null : Bookmark(stored);
        var label = request.Label.Trim();
        if (previous is not null && previous.Minute == request.Minute && previous.Label == label && previous.ZoneId == request.ZoneId && previous.Note == request.Note)
            return previous; // Safe retry after a lost create/update response.
        if (request.Revision != (previous?.Revision ?? 0)) throw new ScenarioConflictException("Bookmark changed. Reload it before editing.");
        if (stored is null && await db.Events.CountAsync(e => e.SimulationRunId == runId && e.Type == BookmarkType, token) >= 200)
            throw Invalid("bookmarks", "A run can retain at most 200 bookmarks.");
        var value = new BookmarkPayload("1.0", request.Minute, label, request.ZoneId, request.Note, (previous?.Revision ?? 0) + 1);
        if (stored is null)
        {
            stored = new SceneEvent { Id = bookmarkId, SceneId = run.SceneId, SimulationRunId = runId, Type = BookmarkType, IsSynthetic = run.IsSynthetic };
            db.Events.Add(stored);
        }
        stored.OccurredAtUtc = captured.Scenario.Inputs.StartsAtUtc.AddMinutes(request.Minute);
        stored.PayloadJson = JsonSerializer.Serialize(value, Json);
        await db.SaveChangesAsync(token); await transaction.CommitAsync(token);
        return Bookmark(stored);
    }

    public async Task DeleteBookmarkAsync(Guid runId, Guid bookmarkId, int revision, CancellationToken token)
    {
        if (revision <= 0) throw Invalid("revision", "A positive bookmark revision is required.");
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await Lock(runId, token); await Require(runId, token);
        var stored = await db.Events.SingleOrDefaultAsync(e => e.Id == bookmarkId && e.SimulationRunId == runId && e.Type == BookmarkType, token);
        if (stored is not null)
        {
            if (Bookmark(stored).Revision != revision) throw new ScenarioConflictException("Bookmark changed. Reload before deleting.");
            db.Events.Remove(stored); await db.SaveChangesAsync(token);
        }
        await transaction.CommitAsync(token);
    }

    private async Task<(SimulationRun Run, RunInputSnapshot Captured)> Require(Guid runId, CancellationToken token)
    {
        var run = await db.Runs.AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId, token) ?? throw new ScenarioNotFoundException("Run was not found.");
        if (!run.ResultsValid || run.InputSnapshotJson is null || run.ScenarioId is null || run.ScenarioRevision is null)
            throw new ScenarioConflictException("Replay requires a completed run with captured inputs.");
        try
        {
            var captured = JsonSerializer.Deserialize<RunInputSnapshot>(run.InputSnapshotJson, Json);
            if (captured?.SchemaVersion != 1 || captured.Scenario?.Inputs is null || captured.Scenario.Scenario?.Id != run.ScenarioId
                || captured.Scenario.Scenario.Revision != run.ScenarioRevision || captured.Layout?.Layout?.Checkpoints is null
                || captured.Layout.Version is null || captured.Scenario.Inputs.DurationMinutes is < 1 or > 10080) throw new JsonException();
            return (run, captured);
        }
        catch (JsonException) { throw new ScenarioConflictException("Captured replay inputs are missing, invalid or use an unsupported version."); }
    }
    private Task Lock(Guid runId, CancellationToken token) => db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM twin.runs WHERE \"Id\" = {runId} FOR UPDATE", token);
    private async Task<IReadOnlyList<ReplayBookmark>> ReadBookmarks(Guid runId, CancellationToken token) =>
        (await db.Events.AsNoTracking().Where(e => e.SimulationRunId == runId && e.Type == BookmarkType).OrderBy(e => e.OccurredAtUtc).ThenBy(e => e.Id).ToArrayAsync(token)).Select(Bookmark).ToArray();
    private static ReplayBookmark Bookmark(SceneEvent row)
    {
        try
        {
            var value = JsonSerializer.Deserialize<BookmarkPayload>(row.PayloadJson, Json);
            if (value?.ContractVersion != "1.0" || !double.IsFinite(value.Minute) || value.Revision < 1 || string.IsNullOrWhiteSpace(value.Label)) throw new JsonException();
            return new(row.Id, row.SimulationRunId!.Value, value.Minute, value.Label, value.ZoneId, value.Note, value.Revision);
        }
        catch (JsonException) { throw new ScenarioConflictException("A stored bookmark has an invalid payload."); }
    }
    private static void Validate(BookmarkWriteRequest request, RunInputSnapshot captured)
    {
        var errors = new Dictionary<string, string[]>();
        if (!double.IsFinite(request.Minute) || request.Minute < 0 || request.Minute > captured.Scenario.Inputs.DurationMinutes) errors["minute"] = ["Choose a finite minute within the captured horizon."];
        if (string.IsNullOrWhiteSpace(request.Label) || request.Label.Length > 120 || request.Label.Contains('\0')) errors["label"] = ["Use 1–120 characters without NUL."];
        if (request.Note is null || request.Note.Length > 500 || request.Note.Contains('\0')) errors["note"] = ["Use at most 500 characters without NUL."];
        if (request.Revision < 0) errors["revision"] = ["Revision cannot be negative."];
        // A bookmark marks a control, or the check-in desks the run simulated.
        if (request.ZoneId is { } zone && !captured.Layout.Layout.Checkpoints.Any(c => c.Id == zone) && captured.Scenario.Inputs.CheckIn?.AreaId != zone)
            errors["zoneId"] = ["Choose a checkpoint or the simulated check-in desks from the run's captured layout."];
        if (errors.Count > 0) throw new ScenarioValidationException(errors);
    }
    private static ScenarioValidationException Invalid(string field, string message) => new(new Dictionary<string, string[]> { [field] = [message] });
}
