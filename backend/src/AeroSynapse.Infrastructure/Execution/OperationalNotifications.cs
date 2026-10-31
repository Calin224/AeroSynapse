using System.Text.Json;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Management;
using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.DigitalTwin;
using AeroSynapse.Infrastructure.Storage;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace AeroSynapse.Infrastructure.Execution;

// This store coordinates application records only. It never changes simulation inputs or dispatches interventions.
public sealed class OperationalNotifications(TwinDbContext db, ISender sender, TimeProvider clock) : IOperationalNotifications
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<NotificationPage<OperationalAlert>> ListAsync(NotificationFilter filter, CancellationToken token)
    {
        Validate(filter);
        var query = Alerts(filter, dates: true);
        var total = await query.CountAsync(token);
        var rows = await query.OrderByDescending(a => a.LastObservedAtUtc).ThenBy(a => a.Id).Skip(filter.Offset).Take(filter.Limit).ToArrayAsync(token);
        return new(rows.Select(Details).ToArray(), total, filter.Offset, filter.Limit);
    }

    public async Task<NotificationPage<AlertActivity>> ActivityAsync(NotificationFilter filter, CancellationToken token)
    {
        Validate(filter);
        var query = from activity in db.NotificationActivity.AsNoTracking()
                    join alert in Alerts(filter, dates: false) on activity.AlertId equals alert.Id
                    where (!filter.FromUtc.HasValue || activity.RecordedAtUtc >= filter.FromUtc.Value)
                       && (!filter.ToUtc.HasValue || activity.RecordedAtUtc <= filter.ToUtc.Value)
                    select new { Activity = activity, Alert = alert };
        var rows = await query.OrderByDescending(a => a.Activity.RecordedAtUtc).ThenByDescending(a => a.Activity.AlertRevision).ThenBy(a => a.Activity.Id)
            .Skip(filter.Offset).Take(filter.Limit).ToArrayAsync(token);
        return new(rows.Select(row => Activity(row.Activity, row.Alert)).ToArray(), await query.CountAsync(token), filter.Offset, filter.Limit);
    }

    public async Task<AlertDetails> GetAsync(Guid id, CancellationToken token)
    {
        var row = await Alerts(new(), false).SingleOrDefaultAsync(a => a.Id == id, token)
            ?? throw new ScenarioNotFoundException("Synthetic notification was not found.");
        var activity = await db.NotificationActivity.AsNoTracking().Where(a => a.AlertId == id && a.AlertRevision <= row.Revision)
            .OrderByDescending(a => a.AlertRevision).Take(100).ToArrayAsync(token);
        return new(Details(row), activity.Select(a => Activity(a, row)).ToArray());
    }

    public async Task<NotificationReceipt> PublishAsync(PublishQueueAlertsRequest request, CancellationToken token)
    {
        if (request.ScenarioId == Guid.Empty || request.RunId == Guid.Empty || request.FromMinute < 0
            || request.HorizonMinutes is < 15 or > 30 || request.WarningThreshold is < 0 or > 99999
            || request.CriticalThreshold <= request.WarningThreshold || request.CriticalThreshold > 100000)
            throw Invalid("rule", "Choose a scenario/run, a captured origin, a 15–30 minute horizon and 0 ≤ warning < critical ≤ 100000 passengers.");
        var identity = new { action = "publish", request };
        if (request.CommandId == Guid.Empty) throw Invalid("commandId", "A nonempty command ID is required.");
        var previous = await db.NotificationCommands.AsNoTracking().SingleOrDefaultAsync(c => c.Id == request.CommandId, token);
        if (previous is not null) return Replay(previous, request.ScenarioId, identity);
        // KPI reads own their consistent read transaction. Calculate before starting the coordination write transaction.

        var run = await db.Runs.AsNoTracking().SingleOrDefaultAsync(r => r.Id == request.RunId, token)
            ?? throw new ScenarioNotFoundException("Run was not found.");
        var scenario = await db.Scenarios.AsNoTracking().SingleOrDefaultAsync(s => s.Id == request.ScenarioId, token)
            ?? throw new ScenarioNotFoundException("Scenario was not found.");
        if (!scenario.IsSynthetic || !run.IsSynthetic || run.ScenarioId != scenario.Id)
            throw new ScenarioConflictException("Notifications are limited to a matching synthetic scenario and run.");
        var captured = JsonSerializer.Deserialize<RunInputSnapshot>(run.InputSnapshotJson ?? "null", Json)
            ?? throw new ScenarioConflictException("The run has no captured inputs.");
        if (captured.Scenario.Scenario.Id != scenario.Id || !captured.Scenario.Scenario.IsSynthetic
            || captured.Scenario.Scenario.Revision != run.ScenarioRevision)
            throw new ScenarioConflictException("Captured inputs do not match the notification context.");
        var forecast = await sender.Send(new ForecastQueuesQuery(run.Id,
            new(request.FromMinute, request.HorizonMinutes, request.WarningThreshold), request.UseLatest), token);
        if (forecast.RunId != run.Id || forecast.Kind != "simulated_estimate"
            || forecast.QueueThresholdPassengers != request.WarningThreshold || forecast.ToMinute - forecast.FromMinute != request.HorizonMinutes)
            throw new QueueForecasterUnavailableException();
        return await Execute(request.CommandId, request.ScenarioId, identity, async () =>
        {
            if (!await db.Runs.AnyAsync(r => r.Id == request.RunId && r.IsSynthetic && r.ScenarioId == request.ScenarioId, token))
                throw new ScenarioConflictException("The synthetic run is no longer available.");
            var rule = RunFingerprint.Hash(JsonSerializer.SerializeToElement(new { source = "queue_forecast", request.WarningThreshold,
                request.CriticalThreshold, request.HorizonMinutes }, Json), "notification-rule");
            var observation = RunFingerprint.Hash(JsonSerializer.SerializeToElement(new { forecast.FromMinute, forecast.ToMinute }, Json), "notification-observation");
            var affected = new List<Guid>(); int duplicates = 0, stale = 0;
            var now = Now;
            foreach (var series in forecast.Checkpoints)
            {
                var zone = captured.Layout.Layout.Checkpoints.SingleOrDefault(c => c.Id == series.CheckpointId)
                    ?? throw new ScenarioConflictException("Forecast zone is absent from the captured layout.");
                var alert = await db.Notifications.SingleOrDefaultAsync(a => a.RunId == run.Id && a.ZoneId == zone.Id && a.RuleKey == rule, token);
                if (alert is not null && await db.NotificationActivity.AnyAsync(a => a.AlertId == alert.Id && a.ObservationKey == observation, token))
                { duplicates++; affected.Add(alert.Id); continue; }
                if (alert is not null && forecast.FromMinute < alert.LatestFromMinute)
                { stale++; affected.Add(alert.Id); continue; }
                var peak = series.Points.Max(p => p.QueuePassengers);
                var level = peak > request.CriticalThreshold ? "critical" : peak > request.WarningThreshold ? "warning" : "resolved";
                if (alert is null && level == "resolved") continue;
                var threshold = level == "critical" ? request.CriticalThreshold : request.WarningThreshold;
                var trigger = series.Points.FirstOrDefault(p => p.QueuePassengers > threshold)?.Minute;
                var evidence = new NotificationEvidence(forecast.FromMinute, forecast.ToMinute, peak, trigger,
                    forecast.PeriodStartUtc.UtcDateTime, forecast.PeriodEndUtc.UtcDateTime, forecast.ObservationSource, forecast.Formula);
                var kind = "observed";
                if (alert is null)
                {
                    alert = new() { Id = Guid.NewGuid(), IsSynthetic = true, ScenarioId = scenario.Id, ScenarioName = captured.Scenario.Scenario.Name,
                        RunId = run.Id, ZoneId = zone.Id, ZoneName = zone.Name, RuleKey = rule, WarningThreshold = request.WarningThreshold,
                        CriticalThreshold = request.CriticalThreshold, HorizonMinutes = request.HorizonMinutes,
                        FirstObservedAtUtc = now, Revision = 0 };
                    db.Notifications.Add(alert); kind = "created";
                }
                else if (alert.Level != level) kind = level == "resolved" ? "resolved" : alert.Level == "resolved" ? "reopened" : "level_changed";
                if ((level == "critical" && alert.Level != "critical") || kind == "reopened")
                { alert.AcknowledgedAtUtc = null; alert.AcknowledgedBy = ""; }
                alert.Level = level; alert.Revision++; alert.Observations++; alert.LatestFromMinute = forecast.FromMinute;
                alert.LastObservedAtUtc = now;
                alert.SimulationAtUtc = evidence.PeriodStartUtc.AddMinutes((trigger ?? forecast.FromMinute) - forecast.FromMinute);
                alert.EvidenceJson = JsonSerializer.Serialize(evidence, Json);
                Append(alert, kind, "Motor prognoză", "", now, observation);
                affected.Add(alert.Id);
            }
            return new(request.CommandId, affected, duplicates, stale, now);
        }, token);
    }

    public async Task<NotificationReceipt> ActAsync(Guid id, AlertActionRequest value, CancellationToken token)
    {
        if (value.Revision < 1 || value.Action is not ("acknowledge" or "comment" or "assign")) throw Invalid("action", "Choose acknowledge, comment or assign and the current revision.");
        if (string.IsNullOrWhiteSpace(value.Actor) || value.Actor.Trim().Length > 160 || value.Actor.Contains('\0')) throw Invalid("actor", "Supply a declared operator name, 1–160 characters.");
        if (value.Text is null || value.Text.Length > (value.Action == "assign" ? 160 : 2000) || value.Text.Contains('\0')
            || (value.Action == "comment" && string.IsNullOrWhiteSpace(value.Text))) throw Invalid("text", "Supply a valid comment or assignee within the field limits.");
        var request = value with { Actor = value.Actor.Trim(), Text = value.Text.Trim() };
        var identity = await Alerts(new(), false).SingleOrDefaultAsync(a => a.Id == id, token)
            ?? throw new ScenarioNotFoundException("Synthetic notification was not found.");
        return await Execute(request.CommandId, identity.ScenarioId, new { id, request }, async () =>
        {
            var alert = await db.Notifications.SingleAsync(a => a.Id == id, token);
            if (alert.Revision != request.Revision) throw new ScenarioConflictException("The alert changed in another session. Reload it before applying a new action.");
            var now = Now;
            if (request.Action == "acknowledge")
            {
                if (alert.Level == "resolved" || alert.AcknowledgedAtUtc.HasValue) throw new ScenarioConflictException("Only an active, unacknowledged alert can be acknowledged.");
                alert.AcknowledgedAtUtc = now; alert.AcknowledgedBy = request.Actor;
            }
            if (request.Action == "assign") alert.Assignee = request.Text;
            alert.Revision++;
            Append(alert, request.Action == "acknowledge" ? "acknowledged" : request.Action == "comment" ? "commented" : "assigned", request.Actor, request.Text, now);
            return new(request.CommandId, [alert.Id], 0, 0, now);
        }, token);
    }

    private async Task<NotificationReceipt> Execute(Guid commandId, Guid scenarioId, object request, Func<Task<NotificationReceipt>> action, CancellationToken token)
    {
        if (commandId == Guid.Empty) throw Invalid("commandId", "A nonempty command ID is required.");
        var hash = RunFingerprint.Hash(JsonSerializer.SerializeToElement(request, Json), "notification-command");
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        // Always lock command then scenario: concurrent retries and coordination writes have a stable lock order.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"notification-command:" + commandId}, 0))", token);
        var previous = await db.NotificationCommands.AsNoTracking().SingleOrDefaultAsync(c => c.Id == commandId, token);
        if (previous is not null)
            return Replay(previous, scenarioId, request);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"notification-scenario:" + scenarioId}, 0))", token);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM twin.scenarios WHERE \"Id\" = {scenarioId} FOR KEY SHARE", token);
        var receipt = await action();
        db.NotificationCommands.Add(new() { Id = commandId, IsSynthetic = true, ScenarioId = scenarioId, RequestHash = hash, ReceiptJson = JsonSerializer.Serialize(receipt, Json) });
        await db.SaveChangesAsync(token); await transaction.CommitAsync(token);
        return receipt;
    }

    private static NotificationReceipt Replay(NotificationCommand previous, Guid scenarioId, object request) =>
        previous.RequestHash == RunFingerprint.Hash(JsonSerializer.SerializeToElement(request, Json), "notification-command") && previous.ScenarioId == scenarioId
            ? JsonSerializer.Deserialize<NotificationReceipt>(previous.ReceiptJson, Json)!
            : throw new ScenarioConflictException("This command ID was already used with different inputs.");

    private void Append(OperationalNotification alert, string kind, string actor, string text, DateTime now, string? observation = null) =>
        db.NotificationActivity.Add(new() { Id = Guid.NewGuid(), IsSynthetic = true, AlertId = alert.Id, Kind = kind, Actor = actor,
            Text = text, RecordedAtUtc = now, AlertRevision = alert.Revision, ObservationKey = observation, EvidenceJson = alert.EvidenceJson });

    private IQueryable<OperationalNotification> Alerts(NotificationFilter f, bool dates) => db.Notifications.AsNoTracking().Where(a =>
        a.IsSynthetic && db.Runs.Any(r => r.Id == a.RunId && r.IsSynthetic) && db.Scenarios.Any(s => s.Id == a.ScenarioId && s.IsSynthetic)
        && (!f.ScenarioId.HasValue || a.ScenarioId == f.ScenarioId) && (!f.RunId.HasValue || a.RunId == f.RunId)
        && (f.Level == null || a.Level == f.Level) && (!f.Acknowledged.HasValue || a.AcknowledgedAtUtc.HasValue == f.Acknowledged)
        && (!dates || ((!f.FromUtc.HasValue || a.LastObservedAtUtc >= f.FromUtc.Value) && (!f.ToUtc.HasValue || a.LastObservedAtUtc <= f.ToUtc.Value))));
    private static void Validate(NotificationFilter f)
    {
        if (f.Offset < 0 || f.Limit is < 1 or > 100 || f.Level is not (null or "warning" or "critical" or "resolved")
            || f.FromUtc is { Kind: not DateTimeKind.Utc } || f.ToUtc is { Kind: not DateTimeKind.Utc } || f.FromUtc > f.ToUtc)
            throw Invalid("filters", "Use UTC dates in chronological order, a valid level and pagination limit 1–100.");
    }
    private static OperationalAlert Details(OperationalNotification a) => new(a.Id, a.ScenarioId, a.ScenarioName, a.RunId, a.ZoneId, a.ZoneName,
        a.Source, a.WarningThreshold, a.CriticalThreshold, a.HorizonMinutes, a.Level, a.Revision, a.Observations, a.FirstObservedAtUtc,
        a.LastObservedAtUtc, a.SimulationAtUtc, JsonSerializer.Deserialize<NotificationEvidence>(a.EvidenceJson, Json)!, a.Assignee, a.AcknowledgedAtUtc, a.AcknowledgedBy);
    private static AlertActivity Activity(NotificationActivity a, OperationalNotification alert) => new(a.Id, alert.Id, alert.ScenarioId, alert.RunId, alert.ZoneId,
        a.Kind, a.Actor, a.Text, a.RecordedAtUtc, a.AlertRevision, a.ObservationKey is null ? null : JsonSerializer.Deserialize<NotificationEvidence>(a.EvidenceJson, Json));
    private static ScenarioValidationException Invalid(string field, string message) => new(new Dictionary<string, string[]> { [field] = [message] });
}
