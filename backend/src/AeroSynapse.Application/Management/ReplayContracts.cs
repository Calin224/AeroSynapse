using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Storage;

namespace AeroSynapse.Application.Management;

public sealed record ReplayRunInfo(Guid Id, Guid SceneId, Guid ScenarioId, int ScenarioRevision, int Seed,
    string Status, bool ResultsValid, bool IsSynthetic, RunProvenance Provenance);
// Check-in desk samples carry their own snapshot; omitted for controls, so earlier replay hashes are unchanged.
public sealed record ReplaySample(Guid ZoneId, double Minute, DateTime RecordedAtUtc, int QueuedPassengers,
    int ProcessedPassengers, KpiSnapshot? Kpi,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] CheckInSnapshot? CheckIn = null);
public sealed record ReplayData(ReplayRunInfo Run, RunInputSnapshot Captured, IReadOnlyList<ReplaySample> Samples, int? IntervalMinutes);
// Hash the exact UTF-8 payload bytes. Bookmarks are mutable annotations outside the immutable payload.
public sealed record ReplayEnvelope(string ContractVersion, Guid RunId, string ContentHash, string PayloadJson, IReadOnlyList<ReplayBookmark> Bookmarks);
public sealed record ReplayBookmark(Guid Id, Guid RunId, double Minute, string Label, Guid? ZoneId, string Note, int Revision);
public sealed record BookmarkWriteRequest(double Minute, string Label, Guid? ZoneId = null, string Note = "", int Revision = 0);
public interface IReplayService
{
    Task<ReplayEnvelope> GetAsync(Guid runId, CancellationToken token);
    Task<IReadOnlyList<ReplayBookmark>> BookmarksAsync(Guid runId, CancellationToken token);
    Task<ReplayBookmark> WriteBookmarkAsync(Guid runId, Guid bookmarkId, BookmarkWriteRequest request, CancellationToken token);
    Task DeleteBookmarkAsync(Guid runId, Guid bookmarkId, int revision, CancellationToken token);
}
