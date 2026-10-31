using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using AeroSynapse.Domain.DigitalTwin;

namespace AeroSynapse.Application.Contracts;

public sealed record CheckpointInput(Guid CheckpointId, double ServiceRatePerMinute, int StaffCount);
public sealed record SimulationRequest(string ContractVersion, Guid RunId, Guid ScenarioId,
    int ScenarioRevision, Guid LayoutVersionId, DateTimeOffset StartsAtUtc, string TimeZone,
    int DurationMinutes, int Seed, int PassengerCount, double ArrivalRatePerMinute,
    IReadOnlyList<CheckpointInput> Checkpoints,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PassengerGenerationRequest? PassengerGeneration = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] NavigationInput? Navigation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Engine = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? SnapshotIntervalMinutes = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ScheduledEvent>? Events = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CheckInInput? CheckIn = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<StaffSchedule>? StaffSchedules = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JourneyInput? Journey = null);
public sealed record OptimizationRequest(string ContractVersion, Guid OptimizationId, string Objective,
    SimulationRequest Baseline, IReadOnlyList<SimulationRequest> Candidates);
public sealed record SimulationMetric(Guid ZoneId, DateTimeOffset RecordedAtUtc, double MeanWaitMinutes,
    double ThroughputPerMinute, int ProcessedPassengers);
public sealed record SimulationSnapshot(Guid ZoneId, double ElapsedMinutes, int QueuedPassengers, int ProcessedPassengers);
// Integration DTOs; the existing Result/Snapshot contracts remain valid.
public sealed record ZoneSnapshot(string ContractVersion, Guid ZoneId, double ElapsedMinutes, int QueuedPassengers, int ProcessedPassengers);
public sealed record ScenarioSnapshot(string ContractVersion, Guid ScenarioId, int ScenarioRevision,
    Guid LayoutVersionId, DateTimeOffset CapturedAtUtc, DateTimeOffset StartsAtUtc, string TimeZone,
    int DurationMinutes, int Seed, int PassengerCount, double ArrivalRatePerMinute,
    IReadOnlyList<CheckpointInput> Checkpoints,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PassengerGenerationRequest? PassengerGeneration = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] NavigationInput? Navigation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Engine = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? SnapshotIntervalMinutes = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ScheduledEvent>? Events = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CheckInInput? CheckIn = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<StaffSchedule>? StaffSchedules = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JourneyInput? Journey = null)
{
    public SimulationRequest ToSimulationRequest(Guid runId)
    {
        var request = new SimulationRequest(ContractVersion, runId, ScenarioId, ScenarioRevision,
            LayoutVersionId, StartsAtUtc, TimeZone, DurationMinutes, Seed, PassengerCount,
            ArrivalRatePerMinute, Checkpoints.ToArray(), PassengerGeneration, Navigation, Engine, SnapshotIntervalMinutes, Events, CheckIn, StaffSchedules, Journey);
        return ContractJson.Read<SimulationRequest>(JsonSerializer.SerializeToElement(request, ContractJson.Options), "SimulationRequest");
    }
}
public sealed record RunFailure(string Code, string Message);
public sealed record SimulationResult(string ContractVersion, Guid RunId, string Status,
    IReadOnlyList<SimulationMetric> Metrics, IReadOnlyList<SimulationSnapshot> Snapshots, RunFailure? Failure);
public sealed record CandidateScore(Guid RunId, double Score, int Rank);
public sealed record OptimizationResult(string ContractVersion, Guid OptimizationId, string Status, string Objective,
    Guid BaselineRunId, double? BaselineScore, IReadOnlyList<CandidateScore> Candidates,
    Guid? RecommendedRunId, RunFailure? Failure);
public sealed record SimulationResponse(string ContractVersion, Guid RunId, string Status, double SimulatedMinutes,
    IReadOnlyList<SimulationMetric> Metrics, IReadOnlyList<ZoneSnapshot> Snapshots, RunFailure? Failure,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] NavigationResult? NavigationResult = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ProcessResult? ProcessResult = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] KpiResult? KpiResult = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CheckInResult? CheckInResult = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JourneyResult? JourneyResult = null);
public sealed record OptimizationResponse(string ContractVersion, Guid OptimizationId, string Status, string Objective,
    Guid BaselineRunId, double? BaselineScore, IReadOnlyList<CandidateScore> Candidates,
    Guid? RecommendedRunId, RunFailure? Failure);
public sealed record ContractFieldError(string Field, string Code, string Message);
public sealed record ContractErrorResponse(string ContractVersion, string Code, string Message,
    IReadOnlyList<ContractFieldError> Errors);

public static class ContractJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();
    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true
        };
        // JSON Schema integers include 60.0 and 6e1. Normalize these after schema validation.
        options.Converters.Add(new MathematicalIntegerConverter());
        options.Converters.Add(new MathematicalLongConverter());
        options.Converters.Add(new UtcTimestampConverter());
        return options;
    }

    public static T Read<T>(JsonElement payload, string definition)
    {
        if (payload.ValueKind == JsonValueKind.Object && (definition is "SimulationRequest" or "ScenarioSnapshot")
            && payload.TryGetProperty("engine", out var engine) && engine.ValueKind == JsonValueKind.String && engine.GetString() == KpiConventions.Engine)
            definition = "Kpi" + definition;
        else if (payload.ValueKind == JsonValueKind.Object && definition == "SimulationResponse" && payload.TryGetProperty("journeyResult", out _))
            definition = "JourneySimulationResponse";
        else if (payload.ValueKind == JsonValueKind.Object && definition == "SimulationResponse" && payload.TryGetProperty("kpiResult", out _))
            definition = "KpiSimulationResponse";
        else if (payload.ValueKind == JsonValueKind.Object && definition == "ProcessResult" && payload.TryGetProperty("engine", out var processEngine)
            && processEngine.ValueKind == JsonValueKind.String && processEngine.GetString() == KpiConventions.Engine)
            definition = "KpiProcessResult";
        else if (payload.ValueKind == JsonValueKind.Object && (definition is "SimulationRequest" or "ScenarioSnapshot") && payload.TryGetProperty("engine", out _))
            definition = "SimPy" + definition;
        else if (payload.ValueKind == JsonValueKind.Object && definition == "SimulationResponse" && payload.TryGetProperty("processResult", out _))
            definition = "SimPySimulationResponse";
        else if (payload.ValueKind == JsonValueKind.Object && (definition is "SimulationRequest" or "ScenarioSnapshot") && payload.TryGetProperty("navigation", out _))
            definition = "Routed" + definition;
        else if (payload.ValueKind == JsonValueKind.Object && definition == "SimulationResponse" && payload.TryGetProperty("navigationResult",out _))
            definition = "RoutedSimulationResponse";
        else if (payload.ValueKind == JsonValueKind.Object && (definition is "SimulationRequest" or "ScenarioSnapshot") && payload.TryGetProperty("passengerGeneration", out _))
            definition = "Profiled" + definition;
        if (payload.ValueKind == JsonValueKind.Object && definition == "OptimizationRequest"
            && payload.TryGetProperty("baseline", out var baseline) && baseline.ValueKind == JsonValueKind.Object
            && baseline.TryGetProperty("passengerGeneration", out _))
            definition = "ProfiledOptimizationRequest";
        var errors = VersionedContract.Validate(definition, payload);
        if (errors.Count != 0) throw new ContractValidationException(errors);
        return payload.Deserialize<T>(Options)!;
    }

    private sealed class MathematicalIntegerConverter : JsonConverter<int>
    {
        public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.Number || !reader.TryGetDouble(out var value)
                || !double.IsFinite(value) || value != Math.Truncate(value) || value < int.MinValue || value > int.MaxValue)
                throw new JsonException("Must be an integer.");
            return (int)value;
        }
        public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
    }

    private sealed class MathematicalLongConverter : JsonConverter<long>
    {
        public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.Number || !reader.TryGetDecimal(out var value)
                || value != decimal.Truncate(value) || value < long.MinValue || value > long.MaxValue)
                throw new JsonException("Must be an integer.");
            return (long)value;
        }
        public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
    }

    private sealed class UtcTimestampConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetDateTimeOffset();
        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
        {
            var utc = value.UtcDateTime;
            var fraction = utc.ToString("ffffff", CultureInfo.InvariantCulture).TrimEnd('0');
            writer.WriteStringValue(utc.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)
                + (fraction.Length == 0 ? "" : "." + fraction) + "Z");
        }
    }
}

public sealed class ContractValidationException(IReadOnlyList<ContractFieldError> errors)
    : Exception("Invalid contract request.")
{
    public IReadOnlyList<ContractFieldError> Errors { get; } = errors;
}
