using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Management;
using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.DigitalTwin;
using AeroSynapse.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AeroSynapse.Infrastructure.Governance;

namespace AeroSynapse.Infrastructure.Execution;

public sealed class CopilotService(TwinDbContext db, IReplayService replay, ICopilotModel model, ILogger<CopilotService> logger, AiGovernance governance) : ICopilotService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task<CopilotContext> ContextAsync(Guid scenarioId, Guid runId, CancellationToken token)
    {
        var run = await Require(scenarioId, runId, token);
        var captured = Capture(run);
        return new("1.0", scenarioId, runId, run.Status, run.ResultsValid, run.IsSynthetic, run.ScenarioRevision,
            captured?.Layout.Version.Id, captured?.Scenario.Inputs.DurationMinutes, captured?.SnapshotIntervalMinutes,
            captured?.Layout.Layout.Checkpoints.Select(z => new CopilotZone(z.Id, z.Code, z.Name)).ToArray() ?? [],
            captured is null ? "capture_unavailable" : !run.ResultsValid ? "run_not_completed" : null);
    }

    public async Task<CopilotAnswer> AskAsync(Guid scenarioId, Guid runId, CopilotQuestion request, CancellationToken token)
    {
        var answer = await AskCore(scenarioId, runId, request, token);
        var grounded = CopilotEvidencePolicy.Accepts(answer);
        if (!grounded) answer = Missing(answer.Scope, "numeric_evidence_rejected", "Explicația nu are surse numerice suficiente. Consultă datele rulării.");
        await governance.RecordAsync(new() { Kind = "answer_evaluation", Status = grounded ? answer.Status == "answered" ? "grounded" : "explicit_limitation" : "rejected",
            ScenarioId = scenarioId, RunId = runId, PolicyVersion = (await governance.PolicyAsync(token)).Version }, token);
        return answer;
    }
    private async Task<CopilotAnswer> AskCore(Guid scenarioId, Guid runId, CopilotQuestion request, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request.Question) || request.Question.Length > 1000
            || request.Question.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
            throw Invalid("question", "Întrebarea trebuie să conțină între 1 și 1000 caractere, fără caractere de control în afara spațierii uzuale.");
        var run = await Require(scenarioId, runId, token);
        var captured = Capture(run);
        var scope = new CopilotScope(scenarioId, runId, request.ZoneId, request.Minute ?? captured?.Scenario.Inputs.DurationMinutes ?? 0);
        if (captured is null) return Missing(scope, "capture_unavailable", "Snapshot-ul intrărilor lipsește sau nu este valid.");
        if (!captured.Layout.Layout.Checkpoints.Any(z => z.Id == request.ZoneId))
            throw Invalid("zoneId", "Zona nu aparține layout-ului capturat pentru această rulare.");
        if (scope.Minute < 0 || scope.Minute > captured.Scenario.Inputs.DurationMinutes)
            throw Invalid("minute", "Minutul trebuie să fie în intervalul rulării capturate.");
        // IDs in the question cannot widen scope. No history, retrieval tools or mutable scenario input.
        var allowed = new HashSet<Guid> { scenarioId, runId, request.ZoneId };
        if (Regex.Matches(request.Question, @"\b[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}\b").Any(m => !allowed.Contains(Guid.Parse(m.Value))))
            return Missing(scope, "question_context_mismatch", "Întrebarea referă un identificator din afara contextului selectat. Selectează explicit rularea și zona dorite.");
        if (!run.ResultsValid) return Missing(scope, "run_not_completed", $"Rularea are status {run.Status}; nu are rezultate valide finalizate.");
        ReplayEnvelope envelope; ReplayData data;
        try
        {
            envelope = await replay.GetAsync(runId, token);
            data = JsonSerializer.Deserialize<ReplayData>(envelope.PayloadJson, Json) ?? throw new JsonException();
        }
        catch (Exception error) when (error is ScenarioConflictException or JsonException or ContractValidationException or InvalidOperationException)
        { return Missing(scope, "evidence_unavailable", "Datele persistate nu pot fi validate pentru această rulare."); }
        var sample = data.Samples.SingleOrDefault(s => s.ZoneId == scope.ZoneId && s.Minute == scope.Minute);
        if (sample?.Kpi is not { } kpi) return Missing(scope, "sample_missing", "Nu există un snapshot KPI la minutul și zona selectate. Nu interpolez datele lipsă.");
        var facts = Facts(kpi);
        var normalized = Normalize(request.Question);
        var localTopic = Topic(normalized);
        // One observation cannot establish a cause, forecast, intervention benefit or before/after change.
        if (localTopic == "causality")
            return Missing(scope, "causality_not_supported", "Un singur snapshot nu stabilește cauze, predicții sau efectul unei modificări. Folosește rulări comparabile în raportul înainte/după.");
        var selection = await model.SelectTopicAsync(new(scope, request.Question, facts), token);
        // A model cannot evade a known question's evidence requirements by selecting a different topic.
        var topic = localTopic != "unsupported" ? localTopic : selection.Status == "available" ? selection.Topic ?? localTopic : localTopic;
        if (!CopilotTopics.All.Contains(topic)) topic = localTopic;
        if (topic is "unsupported" or "causality")
            return Missing(scope, "question_not_supported", "Pot descrie coada, așteptarea, debitul, utilizarea și riscul de îmbarcare observate în această zonă.", selection.Status);
        string[] ids = topic switch
        {
            "queue" => ["queuedPassengers", "maximumQueue", "processingPassengers", "capacity"],
            "wait" => ["meanWaitMinutes", "p95WaitMinutes", "waitSampleCount"],
            "throughput" => ["throughputPerMinute", "cumulativeThroughputPerMinute", "processedPassengers"],
            "utilization" => ["utilization", "cumulativeUtilization", "capacity"],
            "boarding" => ["boardingLateRisk", "boardingAtRiskCount", "boardingRiskPopulationCount"],
            _ => ["queuedPassengers", "meanWaitMinutes", "processedPassengers", "utilization"]
        };
        var chosen = facts.Where(f => ids.Contains(f.Id)).OrderBy(f => Array.IndexOf(ids, f.Id)).ToArray();
        var enough = topic switch
        {
            "wait" => kpi.WaitSampleCount > 0 && kpi.MeanWaitMinutes is not null && kpi.P95WaitMinutes is not null,
            "throughput" => kpi.ThroughputPerMinute is not null,
            "utilization" => kpi.Utilization is not null,
            "boarding" => kpi.BoardingLateRisk is not null && kpi.BoardingRiskPopulationCount > 0,
            _ => chosen.Length > 0
        };
        var citations = chosen.Select(f => new CopilotCitation(f.Id, f.Label, f.Value, f.Unit,
            scenarioId, runId, scope.ZoneId, scope.Minute, sample.RecordedAtUtc)).ToArray();
        var text = string.Join(" ", chosen.Select(f => $"{f.Label}: {f.Value.ToString("0.##########", CultureInfo.InvariantCulture)} {f.Unit} [{f.Id}]."));
        var answer = (enough ? "Observații din simulare. " : "Nu pot concluziona: eșantionul sau numitorul necesar lipsește. ") + text
            + " Valorile descriu exclusiv acest snapshot; nu demonstrează cauze sau efecte ale unor intervenții.";
        logger.LogInformation("Copilot scenarioId={ScenarioId} runId={RunId} zoneId={ZoneId} minute={Minute} modelStatus={ModelStatus} reason={Reason}",
            scenarioId, runId, scope.ZoneId, scope.Minute, selection.Status, enough ? "observed_metrics" : "insufficient_cohort");
        return new("1.0", scope, enough ? "answered" : "insufficient_data", enough ? "observed_metrics" : "insufficient_cohort", answer,
            selection.Status, envelope.ContentHash, citations);
    }

    private async Task<SimulationRun> Require(Guid scenarioId, Guid runId, CancellationToken token)
    {
        var run = await db.Runs.AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId, token)
            ?? throw new ScenarioNotFoundException("Rularea nu există.");
        if (scenarioId == Guid.Empty || run.ScenarioId != scenarioId)
            throw new ScenarioConflictException("Rularea nu aparține scenariului selectat.");
        return run;
    }
    private static RunInputSnapshot? Capture(SimulationRun run)
    {
        try
        {
            var c = JsonSerializer.Deserialize<RunInputSnapshot>(run.InputSnapshotJson ?? "null", Json);
            return c?.SchemaVersion == 1 && c.Scenario?.Scenario is { } scenario && scenario.Id == run.ScenarioId && scenario.Revision == run.ScenarioRevision
                && scenario.SceneId == run.SceneId
                && c.Layout?.Layout?.Checkpoints is not null && c.Layout.Version is { } version && version.Id == scenario.LayoutVersionId
                && version.SceneId == run.SceneId && c.Layout.Layout.Scene?.Id == run.SceneId
                && c.Layout.Layout.Checkpoints.All(z => z is not null && z.SceneId == run.SceneId && z.Id != Guid.Empty)
                && c.Scenario.Inputs?.DurationMinutes is >= 1 and <= 10080 ? c : null;
        }
        catch (JsonException) { return null; }
    }
    private static CopilotAnswer Missing(CopilotScope scope, string reason, string explanation, string model = "not_requested") =>
        new("1.0", scope, "insufficient_data", reason, "Nu pot concluziona: " + explanation, model, null, []);
    private static ScenarioValidationException Invalid(string field, string message) => new(new Dictionary<string, string[]> { [field] = [message] });
    private static string Normalize(string value) => string.Concat(value.Normalize(NormalizationForm.FormD)
        .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)).ToLowerInvariant();
    private static string Topic(string q)
    {
        if (new[] { "de ce", "cauz", "why", "cause", "daca", "what if", "predict", "prognoz", "forecast", "compar", "crest", "scaz", "reduc", "ar trebui", "should", "recomand", "schimb", "modify", "va fi", "vor fi", "maine", "urmato", "will ", "would ", "effect", "efect", "impact", "future", "tomorrow", "optimiz", "optimis" }.Any(q.Contains)) return "causality";
        if (new[] { "astept", "wait", "p95" }.Any(q.Contains)) return "wait";
        if (new[] { "coad", "cozi", "queue" }.Any(q.Contains)) return "queue";
        if (new[] { "debit", "throughput", "procesat" }.Any(q.Contains)) return "throughput";
        if (new[] { "utiliz", "ocup", "capacity", "capacitat" }.Any(q.Contains)) return "utilization";
        if (new[] { "imbarc", "boarding", "risc" }.Any(q.Contains)) return "boarding";
        if (new[] { "rezum", "sumar", "summary", "metric", "zona", "zone", "situat", "status" }.Any(q.Contains)) return "summary";
        return "unsupported";
    }
    private static CopilotFact[] Facts(KpiSnapshot k)
    {
        var facts = new List<CopilotFact>();
        void Add(string id, string label, double? value, string unit) { if (value is { } v && double.IsFinite(v)) facts.Add(new(id, label, v, unit)); }
        Add("queuedPassengers", "Pasageri în coadă", k.QueuedPassengers, "pasageri");
        Add("maximumQueue", "Maxim coadă până la acest minut", k.MaximumQueue, "pasageri");
        Add("processingPassengers", "În procesare", k.ProcessingPassengers, "pasageri");
        Add("processedPassengers", "Procesați cumulat", k.ProcessedPassengers, "pasageri");
        Add("meanWaitMinutes", "Așteptare medie cumulată", k.MeanWaitMinutes, "minute");
        Add("p95WaitMinutes", "P95 așteptare cumulată", k.P95WaitMinutes, "minute");
        Add("waitSampleCount", "Eșantion așteptare (procesare încheiată)", k.WaitSampleCount, "pasageri");
        Add("throughputPerMinute", "Debit în fereastra KPI", k.ThroughputPerMinute, "pasageri/minut");
        Add("cumulativeThroughputPerMinute", "Debit cumulat", k.CumulativeThroughputPerMinute, "pasageri/minut");
        Add("utilization", "Utilizare în fereastra KPI", k.Utilization, "fracție [0,1]");
        Add("cumulativeUtilization", "Utilizare cumulată", k.CumulativeUtilization, "fracție [0,1]");
        Add("capacity", "Capacitate nominală simultană", k.Capacity, "operatori");
        Add("boardingLateRisk", "Risc întârziere la îmbarcare", k.BoardingLateRisk, "fracție [0,1]");
        Add("boardingAtRiskCount", "La risc de întârziere", k.BoardingAtRiskCount, "pasageri");
        Add("boardingRiskPopulationCount", "Populație evaluată pentru îmbarcare", k.BoardingRiskPopulationCount, "pasageri");
        return facts.ToArray();
    }
}
