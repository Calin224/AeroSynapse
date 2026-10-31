namespace AeroSynapse.Application.Management;

public sealed class CopilotTranscriptOptions
{
    public int DefaultRetentionHours { get; set; } = 24;
    public int MaxRetentionHours { get; set; } = 168;
}
public sealed record CopilotTranscriptCapture(Guid Id, string Kind, CopilotScope Scope, DateTime ReceiptExpiresAtUtc,
    string? Question, CopilotExplanation Explanation);
public sealed record SaveCopilotTranscript(string Receipt, bool HideSensitive = true, int? RetentionHours = null);
public sealed record CopilotTranscript(Guid Id, string Kind, CopilotScope Scope, DateTime CreatedAtUtc, DateTime ExpiresAtUtc,
    bool Redacted, bool SensitiveDetailsHidden, string? Question, CopilotExplanation Explanation);
public sealed record CopilotTranscriptSummary(Guid Id, string Kind, Guid ZoneId, int Minute, DateTime CreatedAtUtc, DateTime ExpiresAtUtc, bool Redacted);
public interface ICopilotTranscriptStore
{
    Task<CopilotTranscript> SaveAsync(CopilotTranscriptCapture capture, bool hideSensitive, int? hours, CancellationToken token);
    Task<IReadOnlyList<CopilotTranscriptSummary>> ListAsync(Guid scenarioId, Guid runId, CancellationToken token);
    Task<CopilotTranscript> GetAsync(Guid scenarioId, Guid runId, Guid id, bool includeSensitive, CancellationToken token);
    Task RedactAsync(Guid scenarioId, Guid runId, Guid id, CancellationToken token);
    Task DeleteAsync(Guid scenarioId, Guid runId, Guid id, CancellationToken token);
    Task<int> PurgeExpiredAsync(CancellationToken token);
}
