using System.Text.Json;

namespace AeroSynapse.Application.Management;

public sealed record NotificationFilter(Guid? ScenarioId = null, Guid? RunId = null, DateTime? FromUtc = null,
    DateTime? ToUtc = null, string? Level = null, bool? Acknowledged = null, int Offset = 0, int Limit = 50);
public sealed record NotificationPage<T>(IReadOnlyList<T> Items, int Total, int Offset, int Limit);
public sealed record NotificationEvidence(int FromMinute, int ToMinute, double PeakQueuePassengers, int? TriggerMinute,
    DateTime PeriodStartUtc, DateTime PeriodEndUtc, string ObservationSource, string Formula);
public sealed record OperationalAlert(Guid Id, Guid ScenarioId, string ScenarioName, Guid RunId, Guid ZoneId, string ZoneName,
    string Source, int WarningThreshold, int CriticalThreshold, int HorizonMinutes, string Level, int Revision, int Observations,
    DateTime FirstObservedAtUtc, DateTime LastObservedAtUtc, DateTime SimulationAtUtc, NotificationEvidence Evidence,
    string Assignee, DateTime? AcknowledgedAtUtc, string AcknowledgedBy, bool IsSynthetic = true);
public sealed record AlertActivity(Guid Id, Guid AlertId, Guid ScenarioId, Guid RunId, Guid ZoneId, string Kind, string Actor,
    string Text, DateTime RecordedAtUtc, int AlertRevision, NotificationEvidence? Evidence = null);
public sealed record AlertDetails(OperationalAlert Alert, IReadOnlyList<AlertActivity> Activity);
public sealed record NotificationReceipt(Guid CommandId, IReadOnlyList<Guid> AlertIds, int Duplicates, int StaleObservations, DateTime RecordedAtUtc);
public sealed record PublishQueueAlertsRequest(Guid CommandId, Guid ScenarioId, Guid RunId, int FromMinute, int HorizonMinutes,
    int WarningThreshold, int CriticalThreshold, bool UseLatest = false);
public sealed record AlertActionRequest(Guid CommandId, int Revision, string Action, string Actor, string Text = "");

public interface IOperationalNotifications
{
    Task<NotificationPage<OperationalAlert>> ListAsync(NotificationFilter filter, CancellationToken token);
    Task<NotificationPage<AlertActivity>> ActivityAsync(NotificationFilter filter, CancellationToken token);
    Task<AlertDetails> GetAsync(Guid id, CancellationToken token);
    Task<NotificationReceipt> PublishAsync(PublishQueueAlertsRequest request, CancellationToken token);
    Task<NotificationReceipt> ActAsync(Guid id, AlertActionRequest request, CancellationToken token);
}
