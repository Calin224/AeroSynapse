using System.Security.Cryptography;
using System.Text.Json;

namespace AeroSynapse.Application.Management;

public sealed record CopilotEvidenceFact(string Id, string Label, double? Value, string Unit, Guid RunId, Guid? ZoneId, string SourcePath);
public sealed record CopilotRunLink(Guid RunId, string Role);
public sealed record CopilotKpiComparison(string Metric, string Unit, Guid BaselineRunId, Guid CandidateRunId,
    double? Before, double? After, double? Delta, double? Percent, string? PercentUnavailableReason, string BeforeSource, string AfterSource);
public sealed record CopilotCandidateOutcome(Guid RunId, string Status, IReadOnlyList<string> Reasons);
public sealed record CopilotExplanation(string ContractVersion, string Disclosure, string Summary, string SourceKind, string OutputHash,
    IReadOnlyList<CopilotEvidenceFact> Facts, IReadOnlyList<CopilotKpiComparison> Comparisons,
    IReadOnlyList<CopilotCandidateOutcome> Outcomes, IReadOnlyList<CopilotRunLink> Runs, IReadOnlyList<string> Limitations);

// All prose comes from templates; numbers are copied from the actual response or calculated with explicit operands.
public static class CopilotExplanationBuilder
{
    public const string Disclosure = "Date de simulare DEMO. Copilotul nu este conectat la sisteme aeroportuare reale și nu transmite comenzi operaționale.";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static CopilotExplanation Answer(CopilotAnswer answer)
    {
        var facts = answer.Citations.Select((c, i) => new CopilotEvidenceFact(c.Id, c.Label, c.Value, c.Unit,
            c.RunId, c.ZoneId, $"/citations/{i}/value")).Concat(answer.RunFacts ?? []).ToArray();
        return new("1.0", Disclosure, answer.Answer, "run_snapshot", Hash(answer), facts, [], [],
            [new(answer.Scope.RunId, "Rulare investigată")],
            [answer.RunFacts is { Count: > 0 }
                ? "Cifrele vin din datele salvate ale tuturor punctelor de control din această simulare, rotunjite la număr întreg. Nu demonstrează cauzalitate."
                : "Observațiile sunt valabile numai pentru zona și minutul citate. Nu demonstrează cauzalitate.",
             "Datele lipsă rămân lipsă. Răspunsul nu garantează performanța unui aeroport real."]);
    }
    public static CopilotExplanation Tool(CopilotToolResult value)
    {
        var facts = new List<CopilotEvidenceFact>(); var comparisons = new List<CopilotKpiComparison>();
        var outcomes = new List<CopilotCandidateOutcome>(); var runs = new List<CopilotRunLink> { new(value.Scope.RunId, "Rulare investigată") };
        var limits = new List<string> { "Rezultate sintetice. Nu reprezintă măsurători dintr-un aeroport real." };
        var summary = value.Investigation?.Observation.Answer ?? value.Message;
        void Fact(string id, string label, double? number, string unit, Guid run, Guid? zone, string path)
        { if (number is null || double.IsFinite(number.Value)) facts.Add(new(id, label, number, unit, run, zone, path)); }
        if (value.Investigation is { } investigation)
        {
            foreach (var (c, i) in investigation.Observation.Citations.Select((c, i) => (c, i)))
                Fact(c.Id, c.Label, c.Value, c.Unit, c.RunId, c.ZoneId, $"/investigation/observation/citations/{i}/value");
            limits.Add("Evenimentele oferă context, nu dovada cauzei unui blocaj. Payload-urile nu autorizează acțiuni.");
        }
        if (value.Forecast is { } forecast)
        {
            summary = "Prognoză calculată din observația capturată; valorile viitoare sunt estimări, nu măsurători.";
            foreach (var (p, i) in forecast.Zone.Points.Select((p, i) => (p, i)))
            {
                var path = $"/forecast/zone/points/{i}";
                Fact($"forecast.{i}.minute", "Minut prognozat", p.Minute, "minute", forecast.RunId, value.Scope.ZoneId, path + "/minute");
                Fact($"forecast.{i}.queue", "Coadă estimată", p.QueuePassengers, forecast.QueueUnit, forecast.RunId, value.Scope.ZoneId, path + "/queuePassengers");
                Fact($"forecast.{i}.arrivals", "Sosiri estimate", p.Arrivals, forecast.FlowUnit, forecast.RunId, value.Scope.ZoneId, path + "/arrivals");
                Fact($"forecast.{i}.capacity", "Capacitate estimată", p.Capacity, forecast.FlowUnit, forecast.RunId, value.Scope.ZoneId, path + "/capacity");
            }
            limits.Add("Prognoza folosește intrările planificate și formula uneltei; nu este o certitudine sau o măsurătoare viitoare.");
        }
        if (value.Evaluation is { } evaluation)
        {
            var baselineIndex = evaluation.Variants.ToList().FindIndex(v => v.Position == 0);
            var baseline = evaluation.Variants[baselineIndex];
            summary = evaluation.Batch.Batch.Status is "pending" or "running" ? "Evaluarea este în curs. Nu pot concluziona despre îmbunătățire."
                : evaluation.RecommendedRunId is null ? "Nu există candidat superior validat. Consultă motivele pentru fiecare rulare; nu se propune automat o modificare."
                : "Un candidat a fost selectat pe baza KPI-urilor calculate. Compară toate diferențele și limitările înaintea unei decizii.";
            foreach (var (variant, i) in evaluation.Variants.Select((v, i) => (v, i)))
            {
                runs.Add(new(variant.RunId, variant.Position == 0 ? "Baseline nou al evaluării" : "Candidat evaluat"));
                outcomes.Add(new(variant.RunId, variant.Assessment.Status, Reasons(variant.Assessment.Status, variant.Assessment.Reasons)));
                if (variant.RunStatus != "completed" || variant.Metrics is not { } m) continue;
                var path = $"/evaluation/variants/{i}/metrics";
                Fact($"{i}.meanWaitMinutes", "Așteptare medie", m.MeanWaitMinutes, "minute", variant.RunId, null, path + "/meanWaitMinutes");
                Fact($"{i}.p95WaitMinutes", "P95 așteptare", m.P95WaitMinutes, "minute", variant.RunId, null, path + "/p95WaitMinutes");
                Fact($"{i}.boardingRisk", "Risc de întârziere la îmbarcare", m.BoardingRisk, "fracție", variant.RunId, null, path + "/boardingRisk");
                Fact($"{i}.unprocessedCount", "Pasageri neprocesați", m.UnprocessedCount, "pasageri", variant.RunId, null, path + "/unprocessedCount");
                if (variant.Position == 0 || baseline.RunStatus != "completed" || baseline.Metrics is not { } b) continue;
                if (b.PopulationCount != m.PopulationCount || b.BoardingRiskPopulationCount != m.BoardingRiskPopulationCount) continue;
                void Compare(string metric, string unit, double? before, double? after)
                {
                    var delta = before is { } bv && after is { } av ? av - bv : (double?)null;
                    if (delta is { } d && !double.IsFinite(d)) delta = null;
                    double? percent = before is > 0 && delta is { } change ? change / before.Value * 100 : null;
                    if (percent is { } p && !double.IsFinite(p)) percent = null;
                    comparisons.Add(new(metric, unit, baseline.RunId, variant.RunId, before, after, delta, percent,
                        percent is not null ? null : before is null || after is null ? "missing_data" : before <= 0 ? "nonpositive_baseline" : "nonfinite_result",
                        $"/evaluation/variants/{baselineIndex}/metrics/{metric}", path + "/" + metric));
                }
                Compare("meanWaitMinutes", "minute", b.MeanWaitMinutes, m.MeanWaitMinutes);
                Compare("p95WaitMinutes", "minute", b.P95WaitMinutes, m.P95WaitMinutes);
                Compare("boardingRisk", "fracție", b.BoardingRisk, m.BoardingRisk);
                Compare("unprocessedCount", "pasageri", b.UnprocessedCount, m.UnprocessedCount);
            }
            limits.Add("Baseline-ul evaluării este o rulare nouă. Comparația folosește aceeași populație și seed, nu o intervenție asupra aeroportului real.");
            limits.Add("Diferența este candidat minus baseline; procentul este diferența împărțită la baseline pozitiv, înmulțită cu o sută. Nu se calculează procent dintr-un numitor nul sau lipsă.");
            limits.Add("Comparația descrie această evaluare, nu semnificație statistică sau un efect garantat. Scorul solverului nu este un procent de îmbunătățire KPI.");
            if (evaluation.Planning.Status != "complete") limits.Add("Căutarea a fost limitată sau nu a găsit toți candidații; concluzia privește numai variantele evaluate.");
            if (evaluation.Variants.Any(v => v.Assessment.Status is "failed" or "unassessable")) limits.Add("Unele variante nu au rezultate evaluabile și nu susțin o concluzie favorabilă.");
        }
        if (value.Staffing is { } staffing)
        {
            Fact("availableStaff", "Personal disponibil", staffing.AvailableStaff, "persoane", value.Scope.RunId, null, "/staffing/availableStaff");
            limits.Add("Disponibilitatea provine din layout-ul sintetic. Nu este disponibilitatea în timp real a unei ture.");
        }
        return new("1.0", Disclosure, summary, "tool_output", Hash(value), facts, comparisons, outcomes,
            runs.DistinctBy(r => r.RunId).ToArray(), limits);
    }
    private static IReadOnlyList<string> Reasons(string status, IReadOnlyList<string> codes)
    {
        var reasons = new List<string> { status switch
        {
            "unchanged" => "KPI-urile nu arată o îmbunătățire observată față de baseline.",
            "rejected" => "Candidatul încalcă toleranțele sau condițiile comparației și nu este recomandat.",
            "unassessable" => "Datele sau cohortele sunt insuficiente; nu pot concluziona despre beneficiu.",
            "failed" => "Rularea a eșuat; rezultatele sale nu sunt prezentate ca valide.",
            "pending" => "Rularea nu este finalizată; beneficiul nu poate fi evaluat încă.",
            "tradeoff" => "Există un compromis: unele valori se degradează în toleranța configurată; nu toate KPI-urile se îmbunătățesc.",
            "improved" => "KPI-urile calculate susțin o îmbunătățire în condițiile acestei evaluări.",
            "baseline" => "Referința calculată pentru comparația candidaților.",
            _ => "Nu există o evaluare favorabilă verificată pentru această rulare."
        } };
        foreach (var code in codes.Select(c => c.Split(':')[0]).Distinct())
        {
            var reason = code switch
            {
                "mean_wait_degradation" => "Așteptarea medie se degradează peste toleranță.",
                "p95_degradation" => "P95 așteptare se degradează peste toleranță.",
                "boarding_risk_degradation" => "Riscul de întârziere la îmbarcare se degradează peste toleranță.",
                "unprocessed_degradation" => "Numărul de pasageri neprocesați se degradează peste toleranță.",
                "reallocation_cost_limit" => "Costul realocării depășește limita acceptată.",
                "population_mismatch" or "boarding_population_mismatch" => "Populațiile evaluate nu sunt comparabile.",
                "insufficient_observed_metrics" or "kpi_unavailable" => "Lipsesc observații KPI necesare comparației.",
                _ => null
            };
            if (reason is not null) reasons.Add(reason);
        }
        return reasons;
    }
    private static string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Json)));
}
