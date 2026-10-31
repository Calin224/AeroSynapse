using AeroSynapse.Application.Management;

namespace AeroSynapse.Infrastructure.Execution;

internal static class CopilotWorkflowEvidence
{
    public static CopilotWorkflowConclusion Conclude(IReadOnlyList<CopilotWorkflowStep> steps, bool optimize)
    {
        var evidence = steps.Where(s => s.Status == "completed" && s.Output is not null)
            .Select(s => (Step: s, Evidence: CopilotExplanationBuilder.Tool(s.Output!))).ToArray();
        var last = evidence.Last(); var evaluation = last.Step.Output!.Evaluation;
        var justified = optimize && evaluation?.Batch.Batch.Status is "completed" or "completed_with_failures"
            && evaluation.RecommendedRunId is { } winner
            && evaluation.Variants.Any(v => v.RunId == winner && v.RunStatus == "completed" && v.Assessment.Status is "improved" or "tradeoff")
            && last.Evidence.Comparisons.Any(c => c.CandidateRunId == winner && c.Delta is < 0);
        var message = justified ? "Recomandare justificată de KPI-urile calculate, prezentate înainte/după. Necesită o decizie separată a operatorului."
            : optimize ? "Neconcludent: nu există o îmbunătățire eligibilă demonstrată. Consultă rezultatele și motivele candidaților."
            : "Neconcludent pentru o intervenție: observațiile și prognoza nu demonstrează beneficiul unei realocări de personal.";
        string Path(CopilotWorkflowStep step, string path) => $"/steps/{step.Position}/output{path}";
        // The final reader supersedes the initial pending evaluation: no duplicate or stale candidate facts.
        var selected = evidence.Where(e => e.Step.Output!.Evaluation is null || e.Step == last.Step).ToArray();
        var explanation = last.Evidence with { SourceKind = "workflow_tool_outputs", Summary = message,
            OutputHash = CopilotWorkflowService.Hash(steps.Where(s => s.Output is not null).Select(s => new { s.Id, s.Output })),
            Facts = selected.SelectMany(e => e.Evidence.Facts.Select(f => f with { Id = e.Step.Id + "." + f.Id, SourcePath = Path(e.Step, f.SourcePath) })).ToArray(),
            Comparisons = last.Evidence.Comparisons.Select(c => c with { BeforeSource = Path(last.Step, c.BeforeSource), AfterSource = Path(last.Step, c.AfterSource) }).ToArray(),
            Runs = selected.SelectMany(e => e.Evidence.Runs).DistinctBy(r => r.RunId).ToArray(),
            Limitations = selected.SelectMany(e => e.Evidence.Limitations).Distinct().Concat([
                "Rolurile urmează un plan versionat determinist. Nu reprezintă opinii independente ale mai multor LLM-uri.",
                "Reproducerea păstrează captura, seed-ul și constrângerile. Alte versiuni de motor sau limite de timp pot schimba rezultatul căutării."]).ToArray() };
        return new(justified ? "justified" : "inconclusive", message, explanation);
    }
}
