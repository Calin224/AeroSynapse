namespace AeroSynapse.Application.Management;

public sealed record CopilotZone(Guid Id, string Code, string Name);
public sealed record CopilotContext(string ContractVersion, Guid ScenarioId, Guid RunId, string Status,
    bool ResultsValid, bool IsSynthetic, int? ScenarioRevision, Guid? LayoutVersionId,
    int? DurationMinutes, int? SnapshotIntervalMinutes, IReadOnlyList<CopilotZone> Zones, string? UnavailableReason);
public sealed record CopilotQuestion(Guid ZoneId, string Question, int? Minute = null);
public sealed record CopilotScope(Guid ScenarioId, Guid RunId, Guid ZoneId, int Minute);
// Values and units come from the persisted KpiSnapshot, never from model output.
public sealed record CopilotFact(string Id, string Label, double Value, string Unit);
public sealed record CopilotCitation(string Id, string Label, double Value, string Unit,
    Guid ScenarioId, Guid RunId, Guid ZoneId, int Minute, DateTime RecordedAtUtc);
public sealed record CopilotQueueAdvice(IReadOnlyList<AgentOption> Options, IReadOnlyList<CopilotEvidenceFact> Facts, string Limitation);
public sealed record CopilotAnswer(string ContractVersion, CopilotScope Scope, string Status, string ReasonCode,
    string Answer, string ModelStatus, string? SourceHash, IReadOnlyList<CopilotCitation> Citations,
    CopilotExplanation? Explanation = null, string? Receipt = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] CopilotQueueAdvice? QueueAdvice = null);
public sealed record CopilotModelInput(CopilotScope Scope, string Question, IReadOnlyList<CopilotFact> Facts);
public sealed record CopilotModelResult(string Status, string? Topic = null, long? InputTokens = null, long? OutputTokens = null);
public interface ICopilotModel
{
    Task<CopilotModelResult> SelectTopicAsync(CopilotModelInput input, CancellationToken token);
}
public interface ICopilotService
{
    Task<CopilotContext> ContextAsync(Guid scenarioId, Guid runId, CancellationToken token);
    Task<CopilotAnswer> AskAsync(Guid scenarioId, Guid runId, CopilotQuestion request, CancellationToken token);
}
