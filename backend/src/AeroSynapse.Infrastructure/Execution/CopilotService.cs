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
    public async Task<CopilotContext> ContextAsync(Guid scenarioId, Guid runId, CancellationToken token, bool busiest = false)
    {
        var run = await Require(scenarioId, runId, token);
        var captured = Capture(run);
        // Without valid evidence the page keeps its own default; questions then explain what is missing.
        var focus = busiest && captured is not null && run.ResultsValid && await Replay(runId, token) is { } loaded
            ? CopilotRunAnswers.Busiest(loaded.Data.Samples.Select(s => s.Kpi).OfType<KpiSnapshot>()) : null;
        return new("1.0", scenarioId, runId, run.Status, run.ResultsValid, run.IsSynthetic, run.ScenarioRevision,
            captured?.Layout.Version.Id, captured?.Scenario.Inputs.DurationMinutes, captured?.SnapshotIntervalMinutes,
            captured?.Layout.Layout.Checkpoints.Select(z => new CopilotZone(z.Id, z.Code, z.Name)).ToArray() ?? [],
            captured is null ? "capture_unavailable" : !run.ResultsValid ? "run_not_completed" : null, focus);
    }

    public async Task<CopilotAnswer> AskAsync(Guid scenarioId, Guid runId, CopilotQuestion request, CancellationToken token)
    {
        var places = new List<string>();
        var answer = await AskCore(scenarioId, runId, request, places, token);
        var grounded = CopilotEvidencePolicy.Accepts(answer, places);
        if (!grounded) answer = Missing(answer.Scope, "numeric_evidence_rejected", "Explicația nu are surse numerice suficiente. Consultă datele rulării.");
        await governance.RecordAsync(new() { Kind = "answer_evaluation", Status = grounded ? answer.Status == "answered" ? "grounded" : "explicit_limitation" : "rejected",
            ScenarioId = scenarioId, RunId = runId, PolicyVersion = (await governance.PolicyAsync(token)).Version }, token);
        return answer;
    }
    /// <param name="places">Filled with the checkpoint names a whole-run answer may mention.</param>
    private async Task<CopilotAnswer> AskCore(Guid scenarioId, Guid runId, CopilotQuestion request, List<string> places, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request.Question) || request.Question.Length > 1000
            || request.Question.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
            throw Invalid("question", "Întrebarea trebuie să conțină între 1 și 1000 caractere, fără caractere de control în afara spațierii uzuale.");
        var run = await Require(scenarioId, runId, token);
        var captured = Capture(run);
        var scope = new CopilotScope(scenarioId, runId, request.ZoneId, request.Minute ?? captured?.Scenario.Inputs.DurationMinutes ?? 0);
        if (captured is null) return Missing(scope, "capture_unavailable", "datele de intrare ale acestei simulări lipsesc sau nu sunt valide.");
        if (!captured.Layout.Layout.Checkpoints.Any(z => z.Id == request.ZoneId))
            throw Invalid("zoneId", "Zona nu aparține layout-ului capturat pentru această rulare.");
        if (scope.Minute < 0 || scope.Minute > captured.Scenario.Inputs.DurationMinutes)
            throw Invalid("minute", "Minutul trebuie să fie în intervalul rulării capturate.");
        // IDs in the question cannot widen scope. No history, retrieval tools or mutable scenario input.
        var allowed = new HashSet<Guid> { scenarioId, runId, request.ZoneId };
        if (Regex.Matches(request.Question, @"\b[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}\b").Any(m => !allowed.Contains(Guid.Parse(m.Value))))
            return Missing(scope, "question_context_mismatch", "întrebarea se referă la un identificator din afara simulării alese. Alege explicit simularea și zona dorite.");
        if (!run.ResultsValid) return Missing(scope, "run_not_completed", $"simularea are statusul {run.Status} și nu are rezultate finalizate.");
        if (await Replay(runId, token) is not ({ } envelope, { } data))
            return Missing(scope, "evidence_unavailable", "datele salvate ale acestei simulări nu pot fi verificate.");
        var normalized = Normalize(request.Question);
        var queueAdvice = new[] { "distribu", "deschid", "recomand" }.Any(normalized.Contains)
            && new[] { "coad", "cozi", "checkpoint", "punct", "control" }.Any(normalized.Contains);
        var zones = captured.Layout.Layout.Checkpoints.Select(c => new CopilotZone(c.Id, c.Code, c.Name)).ToArray();
        if (!queueAdvice && CopilotRunAnswers.Route(normalized, zones) is { } runQuery)
        {
            var names = captured.Layout.Layout.Checkpoints.ToDictionary(c => c.Id, c => string.IsNullOrWhiteSpace(c.Code) ? c.Name : c.Code);
            places.AddRange(names.Values);
            var events = captured.Scenario.Inputs.Events.Where(e => CopilotRunAnswers.EventType(e.Type) is not null)
                .Select(e => new CopilotEventFact(e.Id, CopilotRunAnswers.EventType(e.Type)!, (e.OccurredAtUtc - captured.Scenario.Inputs.StartsAtUtc).TotalMinutes, e.CheckpointId)).ToArray();
            var reply = CopilotRunAnswers.Answer(runQuery.Topic, runId, data.Samples.Select(s => s.Kpi).OfType<KpiSnapshot>().ToArray(), names,
                captured.Layout.Layout.Checkpoints.ToDictionary(c => c.Id, c => c.TerminalId), events, runQuery.ZoneId, runQuery.Value);
            logger.LogInformation("Copilot scenarioId={ScenarioId} runId={RunId} topic={Topic} reason={Reason}", scenarioId, runId, runQuery.Topic, reply.ReasonCode);
            return new("1.0", scope, reply.Status, reply.ReasonCode, reply.Text, "not_requested", envelope.ContentHash, [], RunFacts: reply.Facts);
        }
        var sample = data.Samples.SingleOrDefault(s => s.ZoneId == scope.ZoneId && s.Minute == scope.Minute);
        if (sample?.Kpi is not { } kpi) return Missing(scope, "sample_missing", "simularea nu a salvat date pentru această zonă la acest minut. Alege alt minut.");
        var facts = Facts(kpi);
        if (queueAdvice)
        {
            var advice = QueueAdvice(captured, data, scope);
            var fact = facts.Single(f => f.Id == "queuedPassengers");
            var citation = new CopilotCitation(fact.Id, fact.Label, fact.Value, fact.Unit, scenarioId, runId, scope.ZoneId, scope.Minute, sample.RecordedAtUtc);
            var observation = $"Pasageri în coadă: {fact.Value.ToString("0", CultureInfo.InvariantCulture)} pasageri [queuedPassengers]. ";
            var message = advice.Options.Count > 0
                ? "Propun evaluarea variantelor de mai jos pentru deschiderea punctelor disponibile și redistribuirea pasagerilor care încă așteaptă."
                : "Nu există o variantă de deschidere sau redistribuire justificată de coada și capacitățile disponibile la acest minut.";
            return new("1.0", scope, "answered", "queue_advice", observation + message,
                "not_requested", envelope.ContentHash, [citation], QueueAdvice: advice);
        }
        var localTopic = Topic(normalized);
        // One observation cannot establish a cause, forecast, intervention benefit or before/after change.
        if (localTopic == "causality")
            return Missing(scope, "causality_not_supported", "din această simulare pot spune ce s-a întâmplat, nu ce s-ar întâmpla dacă schimbi ceva. Ca să compari variante, pornește o simulare comparativă în Control Tower. Poți întreba și „Ce ar trebui să schimb?”.");
        var selection = await model.SelectTopicAsync(new(scope, request.Question, facts), token);
        // A model cannot evade a known question's evidence requirements by selecting a different topic.
        var topic = localTopic != "unsupported" ? localTopic : selection.Status == "available" ? selection.Topic ?? localTopic : localTopic;
        if (!CopilotTopics.All.Contains(topic)) topic = localTopic;
        if (topic is "unsupported" or "causality")
            return Missing(scope, "question_not_supported", "întrebarea nu pare să fie despre această simulare. Pot răspunde despre cozi, așteptare, pasageri întârziați la îmbarcare, operatori, evenimente, "
                + $"un anumit punct de control sau un anumit minut. Încearcă: {CopilotRunAnswers.Examples}.", selection.Status);
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
        var answer = (enough ? "" : "Nu pot concluziona: la acest minut nu există încă destule date pentru această întrebare. ") + text
            + " Valorile descriu doar zona și minutul alese.";
        logger.LogInformation("Copilot scenarioId={ScenarioId} runId={RunId} zoneId={ZoneId} minute={Minute} modelStatus={ModelStatus} reason={Reason}",
            scenarioId, runId, scope.ZoneId, scope.Minute, selection.Status, enough ? "observed_metrics" : "insufficient_cohort");
        return new("1.0", scope, enough ? "answered" : "insufficient_data", enough ? "observed_metrics" : "insufficient_cohort", answer,
            selection.Status, envelope.ContentHash, citations);
    }

    private static CopilotQueueAdvice QueueAdvice(RunInputSnapshot captured, ReplayData data, CopilotScope scope)
    {
        const string limitation = "Variante calculate din simulare; nimic nu se aplică automat. Numărul de pasageri este o limită propusă, nu un rezultat garantat. Verifică varianta într-o simulare comparativă.";
        var layout = captured.Layout.Layout;
        var selected = layout.Checkpoints.Single(c => c.Id == scope.ZoneId);
        var controls = layout.Checkpoints.Where(c => c.TerminalId == selected.TerminalId).ToArray();
        var rows = data.Samples.Where(s => s.Minute == scope.Minute && s.Kpi is not null && controls.Any(c => c.Id == s.ZoneId))
            .ToDictionary(s => s.ZoneId, s => s.Kpi!);
        var request = CapturedSimulation.Request(scope.RunId, captured);
        var timeline = new EventTimeline(request);
        var frozen = (request.Events ?? []).Where(e => e.CheckpointId is not null && e.Type != "queue_redistribution")
            .Select(e => e.CheckpointId!.Value).ToHashSet();
        var reserved = captured.Scenario.Inputs.StaffAllocations.Select(a => a.StaffMemberId)
            .Concat(captured.Scenario.Inputs.StaffShifts?.Shifts.Select(s => s.StaffMemberId) ?? [])
            .Concat(captured.Scenario.Inputs.IntegratedResources?.Workers.Select(w => w.StaffMemberId) ?? []).ToHashSet();
        var reserve = Math.Min(50, layout.Staff.Count(s => s.TerminalId == selected.TerminalId && !reserved.Contains(s.Id)));
        var agent = captured.Scenario.Inputs.Agent ?? new ScenarioAgent(true, false, false, TransferMinutes: 5, ReserveOperators: reserve);
        var alreadyAdded = Math.Max(0, rows.Values.Sum(k => k.EffectiveCapacity ?? k.Capacity)
            - request.Checkpoints.Where(c => rows.ContainsKey(c.CheckpointId)).Sum(c => c.StaffCount));
        reserve = Math.Max(0, Math.Min(reserve, agent.Reserve) - alreadyAdded);
        var states = controls.Where(c => rows.ContainsKey(c.Id)).Select(c =>
        {
            var k = rows[c.Id]; var staff = k.EffectiveCapacity ?? k.Capacity;
            return new AgentCheckpointState(c.Id, staff, staff, timeline.Available(c.Id, scope.Minute), c.CapacityPerMinute,
                k.QueuedPassengers, k.ProcessingPassengers, k.ProcessedPassengers, 0, k.MeanWaitMinutes, 0, k.BoardingAtRiskCount, k.NotArrivedPassengers);
        }).ToArray();
        var names = controls.ToDictionary(c => c.Id, c => string.IsNullOrWhiteSpace(c.Code) ? c.Name : c.Code);
        var options = AgentCandidates.QueueActions(states, agent.TransferMinutes, frozen, names, reserve)
            .Where(o => o.FromCheckpointId == scope.ZoneId
                && scope.Minute + 1 + agent.TransferMinutes + Math.Max(1, agent.TransferMinutes) < request.DurationMinutes).ToArray();
        var evidence = controls.Where(c => rows.ContainsKey(c.Id)).SelectMany(c => new[]
        {
            new CopilotEvidenceFact($"queue.{c.Id}", $"{names[c.Id]} · pasageri în coadă", rows[c.Id].QueuedPassengers, "pasageri", scope.RunId, c.Id, $"/runs/{scope.RunId}/kpis?zoneId={c.Id}&fromMinute={scope.Minute}&toMinute={scope.Minute}"),
            new CopilotEvidenceFact($"staff.{c.Id}", $"{names[c.Id]} · operatori la acest minut", rows[c.Id].EffectiveCapacity ?? rows[c.Id].Capacity, "operatori", scope.RunId, c.Id, $"/runs/{scope.RunId}/kpis?zoneId={c.Id}&fromMinute={scope.Minute}&toMinute={scope.Minute}")
        }).ToArray();
        return new(options, evidence, limitation);
    }

    private async Task<(ReplayEnvelope Envelope, ReplayData Data)?> Replay(Guid runId, CancellationToken token)
    {
        try
        {
            var envelope = await replay.GetAsync(runId, token);
            return (envelope, JsonSerializer.Deserialize<ReplayData>(envelope.PayloadJson, Json) ?? throw new JsonException());
        }
        catch (Exception error) when (error is ScenarioConflictException or JsonException or ContractValidationException or InvalidOperationException)
        { return null; }
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
        if (new[] { "utiliz", "ocup", "capacity", "capacitat", "operator" }.Any(q.Contains)) return "utilization";
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
