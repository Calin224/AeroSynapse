using AeroSynapse.Application.Management;

namespace AeroSynapse.API;

internal static class NotificationEndpoints
{
    public static void MapNotificationEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/operational-notifications").WithTags("Synthetic operational notifications").AddEndpointFilter<ScenarioErrorFilter>();
        group.MapGet("", async (Guid? scenarioId, Guid? runId, DateTime? fromUtc, DateTime? toUtc, string? level, bool? acknowledged,
            int? offset, int? limit, IOperationalNotifications notifications, CancellationToken token) =>
            Results.Ok(await notifications.ListAsync(new(scenarioId, runId, fromUtc, toUtc, level, acknowledged, offset ?? 0, limit ?? 50), token)))
            .Document<NotificationPage<OperationalAlert>>("Filter synthetic alerts by scenario, run, recording time, level and acknowledgement");
        group.MapGet("/activity", async (Guid? scenarioId, Guid? runId, DateTime? fromUtc, DateTime? toUtc, string? level, bool? acknowledged,
            int? offset, int? limit, IOperationalNotifications notifications, CancellationToken token) =>
            Results.Ok(await notifications.ActivityAsync(new(scenarioId, runId, fromUtc, toUtc, level, acknowledged, offset ?? 0, limit ?? 50), token)))
            .Document<NotificationPage<AlertActivity>>("Recent audited application activity, including comments and assignments");
        group.MapGet("/{id:guid}", async (Guid id, IOperationalNotifications notifications, CancellationToken token) => Results.Ok(await notifications.GetAsync(id, token)))
            .Document<AlertDetails>("Synthetic notification and its latest 100 activity entries");
        group.MapPost("/queue-forecast", async (PublishQueueAlertsRequest request, IOperationalNotifications notifications, CancellationToken token) =>
            Results.Ok(await notifications.PublishAsync(request, token)))
            .Document<NotificationReceipt>("Recalculate evidence and aggregate notification observations; idempotent by command ID").ProducesProblem(502);
        group.MapPost("/run-queues", async (PublishRunAlertsRequest request, IOperationalNotifications notifications, CancellationToken token) =>
            Results.Ok(await notifications.PublishRunsAsync(request, token)))
            .Document<NotificationReceipt>("Alert on recorded queues over the whole period of one, a scenario's or every completed synthetic run; idempotent by command ID");
        group.MapPost("/{id:guid}/actions", async (Guid id, AlertActionRequest request, IOperationalNotifications notifications, CancellationToken token) =>
            Results.Ok(await notifications.ActAsync(id, request, token)))
            .Document<NotificationReceipt>("Acknowledge, comment or assign within the application only; audited and idempotent");
    }
}
