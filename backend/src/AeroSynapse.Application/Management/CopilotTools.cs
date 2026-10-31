using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using AeroSynapse.Application.Contracts;

namespace AeroSynapse.Application.Management;

public sealed record CopilotToolCall(string Name, JsonElement Arguments);
public sealed record CopilotToolRequest(string Intent, Guid ZoneId, int Minute, CopilotToolCall Call);
public sealed record CopilotToolDefinition(string Name, string Effect, IReadOnlyList<string> Intents, string Description);
public sealed record CopilotEventFact(Guid Id, string Type, double Minute, Guid? CheckpointId);
public sealed record CopilotInvestigation(CopilotAnswer Observation, IReadOnlyList<CopilotEventFact> Events);
public sealed record CopilotForecast(string Kind, string Formula, Guid RunId, int FromMinute, int ToMinute,
    string QueueUnit, string FlowUnit, string TimeUnit, QueueForecastSeries Zone);
public sealed record CopilotToolResult(string ContractVersion, CopilotScope Scope, string Intent, string Tool,
    string Effect, string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CopilotInvestigation? Investigation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CopilotForecast? Forecast = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StaffingSettings? Staffing = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StaffingEvaluationDetails? Evaluation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RecommendationOutcome? Recommendation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CopilotExplanation? Explanation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Receipt = null);
public sealed record CopilotInspectionArguments(string Question);
public sealed record CopilotForecastArguments(int HorizonMinutes = 15, int? QueueThresholdPassengers = null);
public sealed record CopilotEvaluationArguments(Guid CommandId, StaffingProposalRequest Proposal, int CandidateCount = 2, int TimeLimitSeconds = 60);
public sealed record CopilotEvaluationReference(Guid EvaluationId);
public sealed record CopilotNoArguments;
public sealed class CopilotToolRejectedException(string code, string field, string message, int status = 422) : Exception(message)
{
    public string Code { get; } = code;
    public string Field { get; } = field;
    public int Status { get; } = status;
}
public interface ICopilotTools
{
    Task<CopilotToolResult> ExecuteAsync(Guid scenarioId, Guid runId, CopilotToolRequest request, CancellationToken token);
}

// The caller's explicit mode is authority. Questions, event payloads and model output are data only.
// One call per request prevents partial execution of an unchecked multi-step model plan.
public static class CopilotToolPolicy
{
    private static readonly string[] ReadIntents = ["diagnose", "forecast", "optimize"];
    public static readonly IReadOnlyList<CopilotToolDefinition> Catalog = [
        new("inspect_zone", "read_only", ReadIntents, "Metrici observate și metadate de eveniment, fără execuții sau editări."),
        new("forecast_queues", "read_only", ReadIntents, "Prognoză calculată pe 15–30 minute din snapshot-ul selectat."),
        new("staffing_settings", "read_only", ReadIntents, "Personal disponibil și constrângeri pentru revizia capturată."),
        new("read_evaluation", "read_only", ReadIntents, "Citește numai o evaluare cu aceleași intrări de baseline."),
        new("evaluate_staffing", "creates_demo_runs", ["optimize"], "CP-SAT și rulări SimPy independente; nu modifică scenariul original."),
        new("prepare_recommendation", "creates_pending_review", ["optimize"], "Pregătește recomandarea calculată; nu aprobă și nu aplică."),
        new("cancel_evaluation", "cancels_demo_runs", ["optimize"], "Cere anularea evaluării selectate, fără schimbări de personal.")
    ];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true, PropertyNameCaseInsensitive = false, MaxDepth = 16
    };
    public static T Parse<T>(JsonElement value)
    {
        try
        {
            if (value.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(value.GetRawText()) > 32768) throw new JsonException();
            Unique(value);
            return value.Deserialize<T>(Json) ?? throw new JsonException();
        }
        catch (JsonException error)
        { throw new CopilotToolRejectedException("invalid_tool_arguments", error.Path ?? "call.arguments", "Apel invalid: verifică tipurile, câmpurile obligatorii și câmpurile suplimentare. Numele duplicate și planurile cu mai multe apeluri sunt refuzate."); }
    }
    private static void Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject()) { if (!names.Add(property.Name)) throw new JsonException(); Unique(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Unique(item);
    }
    public static CopilotToolDefinition Admit(CopilotToolRequest request)
    {
        var tool = Catalog.SingleOrDefault(t => t.Name == request.Call?.Name)
            ?? throw new CopilotToolRejectedException("tool_not_allowed", "call.name", "Unealta nu este permisă. Aprobarea, aplicarea, editarea, SQL, HTTP arbitrar și shell nu fac parte din catalog.");
        if (!tool.Intents.Contains(request.Intent))
            throw new CopilotToolRejectedException("intent_not_allowed", "intent", "Apel refuzat: modul de diagnostic/prognoză nu autorizează mutații. Optimizarea se lansează prin acțiunea explicită a operatorului.");
        return tool;
    }
}
