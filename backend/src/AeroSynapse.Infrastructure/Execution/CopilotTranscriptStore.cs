using System.Text.Json;
using AeroSynapse.Application.Management;
using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.DigitalTwin;
using AeroSynapse.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AeroSynapse.Infrastructure.Execution;

public sealed class CopilotTranscriptStore(TwinDbContext db, TimeProvider clock, IOptions<CopilotTranscriptOptions> options) : ICopilotTranscriptStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    // PostgreSQL timestamps have microsecond precision. Return the same timestamp before and after a retry.
    private DateTime Now
    {
        get { var ticks = clock.GetUtcNow().UtcTicks; return new(ticks - ticks % 10, DateTimeKind.Utc); }
    }
    public async Task<CopilotTranscript> SaveAsync(CopilotTranscriptCapture capture, bool hideSensitive, int? hours, CancellationToken token)
    {
        var retention = hours ?? options.Value.DefaultRetentionHours;
        if (retention < 1 || retention > options.Value.MaxRetentionHours)
            throw new ScenarioValidationException(new Dictionary<string, string[]> { ["retentionHours"] = ["Retention is outside the configured range."] });
        // Serializes saves and retries against each other and physical run deletion.
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM twin.runs WHERE \"Id\" = {capture.Scope.RunId} FOR UPDATE", token);
        var run = await db.Runs.AsNoTracking().SingleOrDefaultAsync(r => r.Id == capture.Scope.RunId && r.ScenarioId == capture.Scope.ScenarioId, token)
            ?? throw new ScenarioNotFoundException("Run not found in this scenario.");
        var row = await db.CopilotTranscripts.SingleOrDefaultAsync(t => t.Id == capture.Id, token);
        if (row is not null)
        {
            if (row.ExpiresAtUtc <= Now || row.Kind == "deleted") throw new ScenarioNotFoundException("Transcript deleted or expired.");
            if (hideSensitive) { row.Question = null; row.Redacted = true; }
        }
        else
        {
            row = new() { Id = capture.Id, SceneId = run.SceneId, ScenarioId = capture.Scope.ScenarioId, RunId = run.Id,
                ZoneId = capture.Scope.ZoneId, Minute = capture.Scope.Minute, Kind = capture.Kind, CreatedAtUtc = Now,
                ExpiresAtUtc = Now.AddHours(retention), Redacted = hideSensitive || capture.Question is null,
                Question = hideSensitive ? null : capture.Question, ExplanationJson = JsonSerializer.Serialize(capture.Explanation, Json) };
            db.CopilotTranscripts.Add(row);
        }
        await db.SaveChangesAsync(token); await transaction.CommitAsync(token);
        return View(row, false);
    }
    public async Task<IReadOnlyList<CopilotTranscriptSummary>> ListAsync(Guid scenarioId, Guid runId, CancellationToken token) =>
        await db.CopilotTranscripts.AsNoTracking().Where(t => t.ScenarioId == scenarioId && t.RunId == runId && t.ExpiresAtUtc > Now && t.Kind != "deleted")
            .OrderByDescending(t => t.CreatedAtUtc).Take(100)
            .Select(t => new CopilotTranscriptSummary(t.Id, t.Kind, t.ZoneId, t.Minute, t.CreatedAtUtc, t.ExpiresAtUtc, t.Redacted)).ToArrayAsync(token);
    public async Task<CopilotTranscript> GetAsync(Guid scenarioId, Guid runId, Guid id, bool includeSensitive, CancellationToken token) =>
        View(await Find(scenarioId, runId, id, token), includeSensitive);
    public async Task RedactAsync(Guid scenarioId, Guid runId, Guid id, CancellationToken token)
    {
        var count = await Query(scenarioId, runId, id).ExecuteUpdateAsync(set => set.SetProperty(t => t.Question, (string?)null).SetProperty(t => t.Redacted, true), token);
        if (count == 0) throw new ScenarioNotFoundException("Transcript not found or expired.");
    }
    public async Task DeleteAsync(Guid scenarioId, Guid runId, Guid id, CancellationToken token)
    {
        // Erase content now; retain a short-lived revocation marker so an old receipt cannot restore it.
        var until = Now.AddMinutes(15);
        var count = await Query(scenarioId, runId, id).ExecuteUpdateAsync(set => set
            .SetProperty(t => t.Kind, "deleted").SetProperty(t => t.Question, (string?)null).SetProperty(t => t.Redacted, true)
            .SetProperty(t => t.ExplanationJson, "{}").SetProperty(t => t.ZoneId, Guid.Empty).SetProperty(t => t.Minute, 0)
            .SetProperty(t => t.ExpiresAtUtc, t => t.ExpiresAtUtc < until ? t.ExpiresAtUtc : until), token);
        if (count == 0) throw new ScenarioNotFoundException("Transcript not found or expired.");
    }
    public Task<int> PurgeExpiredAsync(CancellationToken token) => db.CopilotTranscripts.Where(t => t.ExpiresAtUtc <= Now).ExecuteDeleteAsync(token);
    private IQueryable<CopilotTranscriptRecord> Query(Guid scenarioId, Guid runId, Guid id) =>
        db.CopilotTranscripts.Where(t => t.Id == id && t.ScenarioId == scenarioId && t.RunId == runId && t.ExpiresAtUtc > Now && t.Kind != "deleted");
    private async Task<CopilotTranscriptRecord> Find(Guid scenarioId, Guid runId, Guid id, CancellationToken token) =>
        await Query(scenarioId, runId, id).AsNoTracking().SingleOrDefaultAsync(token) ?? throw new ScenarioNotFoundException("Transcript not found or expired.");
    private static CopilotTranscript View(CopilotTranscriptRecord row, bool includeSensitive) => new(row.Id, row.Kind,
        new(row.ScenarioId, row.RunId, row.ZoneId, row.Minute), row.CreatedAtUtc, row.ExpiresAtUtc, row.Redacted,
        !includeSensitive || row.Redacted, includeSensitive && !row.Redacted ? row.Question : null,
        JsonSerializer.Deserialize<CopilotExplanation>(row.ExplanationJson, Json)!);
}
