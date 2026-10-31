using System.Text.Json;

namespace AeroSynapse.Application.Management;

public sealed record PlaybookAgent(string Id, string Description, IReadOnlyList<string> Tools);
public sealed record PlaybookStep(string Id, string Agent, string? Tool, string Description);
public sealed record CopilotPlaybook(string Id, string Version, string Name, string Intent, string Trigger,
    IReadOnlyList<PlaybookStep> Steps, IReadOnlyList<string> StopConditions);
public sealed record StartCopilotWorkflow(Guid CommandId, string PlaybookId, string PlaybookVersion, Guid ZoneId, int Minute,
    int HorizonMinutes = 15, StaffingProposalRequest? Proposal = null, int CandidateCount = 2, int TimeLimitSeconds = 120);
public sealed record ReplayCopilotWorkflow(Guid CommandId);
public sealed record CopilotWorkflowStep(int Position, string Id, string Agent, string? Tool, string Status,
    int Attempts, int Polls, DateTime? StartedAtUtc, DateTime? FinishedAtUtc, string? Code, JsonElement? Arguments, CopilotToolResult? Output);
public sealed record CopilotWorkflowConclusion(string Status, string Message, CopilotExplanation? Explanation);
public sealed record CopilotWorkflow(Guid Id, string ContractVersion, CopilotScope Scope, string Status, int Revision,
    DateTime CreatedAtUtc, DateTime DeadlineAtUtc, DateTime? FinishedAtUtc, bool CancellationRequested, bool CancellationPending,
    string? StopCode, Guid? ReplayOfId, string ManifestHash, string SourceHash, int Seed, string? EngineVersion, string? EngineBuild,
    Guid? EvaluationId, StartCopilotWorkflow Request, CopilotPlaybook Playbook,
    IReadOnlyList<CopilotWorkflowStep> Steps, CopilotWorkflowConclusion? Conclusion, int? PolicyVersion = null, string? PromptVersion = null);
public sealed record CopilotWorkflowSummary(Guid Id, string PlaybookId, Guid ZoneId, int Minute, string Status, DateTime CreatedAtUtc);
public interface ICopilotWorkflows
{
    Task<CopilotWorkflow> StartAsync(Guid scenarioId, Guid runId, StartCopilotWorkflow request, CancellationToken token);
    Task<CopilotWorkflow> GetAsync(Guid scenarioId, Guid runId, Guid id, CancellationToken token);
    Task<IReadOnlyList<CopilotWorkflowSummary>> ListAsync(Guid scenarioId, Guid runId, CancellationToken token);
    Task<CopilotWorkflow> CancelAsync(Guid scenarioId, Guid runId, Guid id, CancellationToken token);
    Task<CopilotWorkflow> ReplayAsync(Guid scenarioId, Guid runId, Guid id, ReplayCopilotWorkflow request, CancellationToken token);
}

// Deterministic specialists cooperate through a fixed server plan. No prompt can add a role or tool.
public static class CopilotPlaybookPolicy
{
    public static readonly IReadOnlyList<PlaybookAgent> Agents = [
        new("investigator", "Verifică observațiile zonei capturate.", ["inspect_zone"]),
        new("forecaster", "Calculează prognoza sintetică limitată.", ["forecast_queues"]),
        new("optimizer", "Evaluează numai constrângerile confirmate de operator.", ["staffing_settings", "evaluate_staffing"]),
        new("reviewer", "Verifică rezultatele calculate; nu aprobă și nu aplică.", ["read_evaluation"]),
        new("coordinator", "Oprește numai evaluarea deținută de workflow.", ["cancel_evaluation"]) ];
    private static readonly PlaybookStep Observe = new("observe", "investigator", "inspect_zone", "Citește KPI și metadate de eveniment din run-ul selectat.");
    private static readonly PlaybookStep Forecast = new("forecast", "forecaster", "forecast_queues", "Calculează prognoza zonei, fără schimbări de scenariu.");
    private static readonly PlaybookStep Review = new("conclude", "reviewer", null, "Justifică numeric concluzia sau o declară neconcludentă.");
    private static readonly string[] Stops = ["Operatorul solicită anularea.", "Expiră bugetul de timp, inclusiv așteptarea.",
        "Lipsesc dovezi, contextul diferă sau un policy gate refuză apelul.", "O unealtă eșuează ori nu respectă contractul.",
        "Pașii se termină; nu se aprobă și nu se aplică automat recomandări."];
    public static readonly IReadOnlyList<CopilotPlaybook> Catalog = [
        new("queue-diagnosis", "1.0", "Diagnostic și prognoză cozi", "diagnose",
            "Cerere explicită a operatorului pentru o zonă/minut dintr-un run DEMO finalizat, cu loc pentru orizontul prognozei.",
            [Observe, Forecast, Review], Stops),
        new("staffing-assessment", "1.0", "Evaluare controlată de personal", "optimize",
            "Operatorul confirmă explicit constrângerile de personal pentru revizia capturată a scenariului sintetic.",
            [Observe, Forecast, new("settings", "optimizer", "staffing_settings", "Verifică revizia și personalul disponibil."),
             new("evaluate", "optimizer", "evaluate_staffing", "Lansează CP-SAT și rulări DEMO independente cu același seed."),
             new("await-evaluation", "reviewer", "read_evaluation", "Observă evaluarea până la un rezultat terminal, în buget."), Review], Stops) ];
    public static CopilotPlaybook Definition(string id, string version) => Catalog.SingleOrDefault(p => p.Id == id && p.Version == version)
        ?? throw new CopilotToolRejectedException("playbook_not_allowed", "playbookId/playbookVersion", "Playbook-ul sau versiunea nu este permisă.");
    public static void Admit(CopilotPlaybook playbook, string agent, string tool)
    {
        var allowed = Agents.SingleOrDefault(a => a.Id == agent);
        if (allowed is null || !allowed.Tools.Contains(tool) || !playbook.Steps.Any(s => s.Agent == agent && s.Tool == tool)
            && !(agent == "coordinator" && tool == "cancel_evaluation" && playbook.Intent == "optimize"))
            throw new CopilotToolRejectedException("playbook_tool_denied", "agent/tool", "Rolul nu poate accesa această unealtă în playbook.");
        CopilotToolPolicy.Admit(new(playbook.Intent, Guid.Empty, 0, new(tool, JsonSerializer.SerializeToElement(new { }))));
    }
}
