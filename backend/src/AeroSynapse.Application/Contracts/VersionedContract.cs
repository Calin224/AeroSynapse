using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace AeroSynapse.Application.Contracts;

public static class VersionedContract
{
    public const string Version = "1.0";
    private static readonly JsonNode Document = LoadDocument();
    private static readonly ConcurrentDictionary<string, Lazy<JsonSchema>> Schemas = new();
    private static readonly IReadOnlyDictionary<string, string> Messages = new Dictionary<string, string>
    {
        ["required"] = "Field is required.", ["unknown_field"] = "Unknown field.",
        ["invalid_type"] = "Value has an invalid JSON type.", ["out_of_range"] = "Value is outside the allowed range.",
        ["invalid_format"] = "Value has an invalid format.", ["unsupported_version"] = "Unsupported contract version.",
        ["invalid_value"] = "Value is not allowed.", ["invalid_timezone"] = "A supported IANA time zone is required.",
        ["duplicate_id"] = "Identifiers must be unique.", ["event_conflict"] = "The event contradicts the scheduled timeline.",
        ["comparison_mismatch"] = "Compared inputs must use the same layout, horizon, time zone, demand and seed.",
        ["invalid_result"] = "Result fields contradict the run status or comparison.",
        ["malformed_json"] = "Malformed JSON.", ["duplicate_field"] = "Duplicate JSON field.",
        ["unsupported_media_type"] = "Content-Type must be application/json."
    };

    private static JsonNode LoadDocument()
    {
        using var stream = typeof(VersionedContract).Assembly.GetManifestResourceStream("AeroSynapse.Contracts.v1.json")!;
        var document = JsonNode.Parse(stream)!;
        using var passengers = typeof(VersionedContract).Assembly.GetManifestResourceStream("AeroSynapse.Contracts.passengers.v1.json")!;
        foreach (var definition in JsonNode.Parse(passengers)!["$defs"]!.AsObject()) document["$defs"]![definition.Key] = definition.Value!.DeepClone();
        using var navigation = typeof(VersionedContract).Assembly.GetManifestResourceStream("AeroSynapse.Contracts.navigation.v1.json")!;
        foreach (var definition in JsonNode.Parse(navigation)!["$defs"]!.AsObject()) document["$defs"]![definition.Key] = definition.Value!.DeepClone();
        using var processes = typeof(VersionedContract).Assembly.GetManifestResourceStream("AeroSynapse.Contracts.processes.v1.json")!;
        foreach (var definition in JsonNode.Parse(processes)!["$defs"]!.AsObject()) document["$defs"]![definition.Key] = definition.Value!.DeepClone();
        using var kpis = typeof(VersionedContract).Assembly.GetManifestResourceStream("AeroSynapse.Contracts.kpis.v1.json")!;
        foreach (var definition in JsonNode.Parse(kpis)!["$defs"]!.AsObject()) document["$defs"]![definition.Key] = definition.Value!.DeepClone();
        using var events = typeof(VersionedContract).Assembly.GetManifestResourceStream("AeroSynapse.Contracts.events.v1.json")!;
        foreach (var definition in JsonNode.Parse(events)!["$defs"]!.AsObject()) document["$defs"]![definition.Key] = definition.Value!.DeepClone();
        using var staffing = typeof(VersionedContract).Assembly.GetManifestResourceStream("AeroSynapse.Contracts.staffing.v1.json")!;
        foreach (var definition in JsonNode.Parse(staffing)!["$defs"]!.AsObject()) document["$defs"]![definition.Key] = definition.Value!.DeepClone();
        using var resources = typeof(VersionedContract).Assembly.GetManifestResourceStream("AeroSynapse.Contracts.resources.v1.json")!;
        foreach (var definition in JsonNode.Parse(resources)!["$defs"]!.AsObject()) document["$defs"]![definition.Key] = definition.Value!.DeepClone();
        using var forecast = typeof(VersionedContract).Assembly.GetManifestResourceStream("AeroSynapse.Contracts.forecast.v1.json")!;
        foreach (var definition in JsonNode.Parse(forecast)!["$defs"]!.AsObject()) document["$defs"]![definition.Key] = definition.Value!.DeepClone();
        using var anomaly = typeof(VersionedContract).Assembly.GetManifestResourceStream("AeroSynapse.Contracts.anomaly.v1.json")!;
        foreach (var definition in JsonNode.Parse(anomaly)!["$defs"]!.AsObject()) document["$defs"]![definition.Key] = definition.Value!.DeepClone();
        using var journeys = typeof(VersionedContract).Assembly.GetManifestResourceStream("AeroSynapse.Contracts.journeys.v1.json")!;
        foreach (var definition in JsonNode.Parse(journeys)!["$defs"]!.AsObject()) document["$defs"]![definition.Key] = definition.Value!.DeepClone();
        return document;
    }

    public static JsonNode Schema(string definition)
    {
        if (Document["$defs"]?[definition] is null) throw new ArgumentException("Unknown contract definition.", nameof(definition));
        var schema = Document.DeepClone();
        schema["$ref"] = "#/$defs/" + definition;
        return schema;
    }

    public static ContractFieldError Error(string field, string code) => new(field, code, Messages[code]);
    public static ContractErrorResponse Response(IReadOnlyList<ContractFieldError> errors) =>
        new(Version, "validation_failed", "Invalid contract request.", errors);

    public static IReadOnlyList<ContractFieldError> Validate(string definition, JsonElement payload)
    {
        var errors = new List<ContractFieldError>();
        try { errors.AddRange(DuplicateFields(payload)); }
        catch (InvalidOperationException) { return [Error("", "malformed_json")]; }
        if (errors.Count != 0) return errors;
        ValidateNumbers(payload, "", errors);
        if (errors.Count != 0) return errors.OrderBy(x => x.Field, StringComparer.Ordinal).ToArray();
        var schema = Schemas.GetOrAdd(definition, name => new Lazy<JsonSchema>(() => JsonSchema.Build(
            JsonSerializer.SerializeToElement(Schema(name)), new BuildOptions { Dialect = Dialect.Draft202012, SchemaRegistry = new() }))).Value;
        var result = schema.Evaluate(payload, new EvaluationOptions { OutputFormat = OutputFormat.Hierarchical });
        if (!result.IsValid) Collect(result);
        if (errors.Count == 0)
        {
            ValidateIntegers(definition, payload, "", errors);
            if (errors.Count == 0)
            {
                ValidateIdentifiers(payload, "", errors);
                ValidateSemantics(definition, payload, "", errors);
            }
        }
        return errors.Distinct().OrderBy(x => x.Field, StringComparer.Ordinal).ThenBy(x => x.Code, StringComparer.Ordinal).ToArray();

        void Collect(EvaluationResults current, string prefix = "")
        {
            if (current.IsValid) return;
            var field = prefix + current.InstanceLocation.ToString();
            if (current.Errors is not null)
                foreach (var keyword in current.Errors.Keys)
                {
                    // False additional-property subschemas have an empty keyword; their parent reports the field.
                    if (keyword is "" or "properties" or "items" or "$ref") continue;
                    var node = SchemaAt(definition, field);
                    // A failed const inside a successful "not" is a negated diagnostic, not another field error.
                    if (keyword == "const" && node["const"] is null) continue;
                    var value = At(payload, field);
                    if (keyword == "anyOf" && ((value.ValueKind == JsonValueKind.Object && node["type"]?.ToString() == "object")
                        || (value.ValueKind == JsonValueKind.Array && node["type"]?.ToString() == "array")))
                    {
                        // Report the concrete nullable branch's field error, rather than a misleading union type error.
                        var concrete = node.DeepClone().AsObject();
                        concrete["$defs"] = Document["$defs"]!.DeepClone();
                        var concreteSchema = JsonSchema.Build(JsonSerializer.SerializeToElement(concrete),
                            new BuildOptions { Dialect = Dialect.Draft202012, SchemaRegistry = new() });
                        Collect(concreteSchema.Evaluate(value, new EvaluationOptions { OutputFormat = OutputFormat.Hierarchical }), field);
                        return;
                    }
                    if (keyword == "required" && value.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var required in node["required"]!.AsArray())
                            if (!value.TryGetProperty(required!.GetValue<string>(), out _))
                                errors.Add(Error(field + "/" + Escape(required.GetValue<string>()), "required"));
                    }
                    else if (keyword == "additionalProperties" && value.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var property in value.EnumerateObject())
                            if (node["properties"]?[property.Name] is null)
                                errors.Add(Error(field + "/" + Escape(property.Name), "unknown_field"));
                    }
                    else errors.Add(Error(field, keyword switch
                    {
                        "const" when field.EndsWith("/contractVersion", StringComparison.Ordinal) => "unsupported_version",
                        "type" or "anyOf" => "invalid_type",
                        "minimum" or "maximum" or "exclusiveMinimum" or "exclusiveMaximum" or "minItems" or "maxItems" or "minLength" or "maxLength" => "out_of_range",
                        "pattern" or "format" or "not" => "invalid_format",
                        _ => "invalid_value"
                    }));
                    if (keyword == "anyOf") return; // Report the union once, without branch diagnostics.
                }
            if (current.Details is not null) foreach (var detail in current.Details) Collect(detail, prefix);
        }
    }

    private static JsonNode SchemaAt(string definition, string pointer)
    {
        JsonNode node = Document["$defs"]![definition]!;
        foreach (var part in Parts(pointer))
        {
            node = Resolve(node);
            node = node["type"]?.ToString() == "array" ? node["items"]! : node["properties"]![part]!;
        }
        return Resolve(node);
    }
    private static JsonNode Resolve(JsonNode node)
    {
        if (node["$ref"] is not null)
            return Resolve(Document["$defs"]![node["$ref"]!.GetValue<string>().Split('/').Last()]!);
        // Nullable objects still have a concrete shape for field diagnostics and exact integer checks.
        if (node["anyOf"] is JsonArray alternatives)
        {
            var nonNull = alternatives.Select(item => Resolve(item!)).Where(item => item["type"]?.ToString() != "null").ToArray();
            if (nonNull.Length == 1) return nonNull[0];
        }
        return node;
    }
    private static string Escape(string value) => value.Replace("~", "~0").Replace("/", "~1");
    private static IEnumerable<string> Parts(string pointer) => pointer.Split('/').Skip(1).Select(p => p.Replace("~1", "/").Replace("~0", "~"));
    private static JsonElement At(JsonElement value, string pointer)
    {
        foreach (var part in Parts(pointer)) value = value.ValueKind == JsonValueKind.Array ? value[int.Parse(part, CultureInfo.InvariantCulture)] : value.GetProperty(part);
        return value;
    }
    private static bool Utc(string value, out DateTime timestamp) => DateTime.TryParseExact(value,
        ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFF'Z'"], CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out timestamp);

    private static void ValidateSemantics(string definition, JsonElement value, string path, List<ContractFieldError> errors)
    {
        if (definition == "QueueForecastRequest")
        {
            QueueForecastValidation.Request(value.Deserialize<QueueForecastRequest>(ContractJson.Options)!, path, errors);
            return;
        }
        if (definition == "QueueForecastResult")
        {
            if (!Utc(value.GetProperty("periodStartUtc").GetString()!, out _) || !Utc(value.GetProperty("periodEndUtc").GetString()!, out _))
            {
                errors.Add(Error(path, "invalid_result"));
                return;
            }
            if (!QueueForecastValidation.ValidResult(value.Deserialize<QueueForecastResult>(ContractJson.Options)!))
                errors.Add(Error(path, "invalid_result"));
            return;
        }
        if (definition == "AnomalyDetectionRequest")
        {
            AnomalyValidation.Request(value.Deserialize<AnomalyDetectionRequest>(ContractJson.Options)!, path, errors);
            return;
        }
        if (definition == "AnomalyDetectionResult")
        {
            if (!AnomalyValidation.ValidResult(value.Deserialize<AnomalyDetectionResult>(ContractJson.Options)!)) errors.Add(Error(path, "invalid_result"));
            return;
        }
        if (definition is "AnomalyEvaluationRequest" or "AnomalyCaseRequest")
        {
            AnomalyValidation.Settings(value.GetProperty("settings").Deserialize<AnomalySettings>(ContractJson.Options)!, 1, path, errors);
            return;
        }
        if (definition is "AnomalyEvaluationResult" or "AnomalyCaseResult")
        {
            AnomalyValidation.Report(definition, value, path, errors);
            return;
        }
        if (definition == "QueuePredictionResult")
        {
            if (!QueuePredictionValidation.ValidResult(value.Deserialize<QueuePredictionResult>(ContractJson.Options)!))
                errors.Add(Error(path, "invalid_result"));
            return;
        }
        if (definition == "IntegratedResourceConfig")
        {
            ResourceValidation.Config(value.Deserialize<AeroSynapse.Domain.DigitalTwin.IntegratedResourceConfig>(ContractJson.Options)!, path, errors);
            return;
        }
        if (definition == "ResourceConfigWriteRequest")
        {
            if (value.GetProperty("resources").ValueKind != JsonValueKind.Null)
                ValidateSemantics("IntegratedResourceConfig", value.GetProperty("resources"), path + "/resources", errors);
            return;
        }
        if (definition == "ResourceSolveRequest")
        {
            ResourceValidation.Request(value.Deserialize<ResourceSolveRequest>(ContractJson.Options)!, path, errors);
            return;
        }
        if (definition == "ResourceSolveResult")
        {
            var result = value.Deserialize<ResourceSolveResult>(ContractJson.Options)!;
            ResourceValidation.Request(result.Constraints, path + "/constraints", errors);
            if (errors.Count == 0 && !ResourceValidation.Valid(result.Constraints, result)) errors.Add(Error(path, "invalid_result"));
            return;
        }
        if (definition == "ParetoSearchRequest")
        {
            var request = value.Deserialize<ParetoSearchRequest>(ContractJson.Options)!;
            StaffingValidation.Request(request.Constraints, path + "/constraints", errors);
            if (!ParetoModel.Valid(request.Options) || request.Constraints.Checkpoints.Sum(cp => (long)Math.Min(Math.Min(cp.PhysicalLimit, cp.MaximumStaff), request.Constraints.AvailableStaff) + 1) > 50000)
                errors.Add(Error(path + "/options", "out_of_range"));
            return;
        }
        if (definition == "ParetoSearchResult")
        {
            var result = value.Deserialize<ParetoSearchResult>(ContractJson.Options)!;
            if (!ParetoModel.ValidResult(result.Request, result)) errors.Add(Error(path, "invalid_result"));
            return;
        }
        if (definition == "StaffingCandidateSearchRequest")
        {
            StaffingValidation.Request(value.Deserialize<StaffingCandidateSearchRequest>(ContractJson.Options)!.Constraints, path + "/constraints", errors);
            return;
        }
        if (definition == "StaffingCandidateSearchResult")
        {
            var result = value.Deserialize<StaffingCandidateSearchResult>(ContractJson.Options)!;
            StaffingValidation.CandidateResult(result, path, errors);
            return;
        }
        if (definition == "ShiftPlanRequest")
        {
            ShiftPlanValidation.Request(value.Deserialize<ShiftPlanRequest>(ContractJson.Options)!, path, errors);
            return;
        }
        if (definition == "ShiftPlanResult")
        {
            var result = value.Deserialize<ShiftPlanResult>(ContractJson.Options)!;
            ShiftPlanValidation.Request(result.Request, path + "/request", errors);
            if (errors.Count == 0 && !ShiftPlanValidation.Valid(result)) errors.Add(Error(path, "invalid_result"));
            return;
        }
        if (definition == "StaffingSolveRequest")
        {
            StaffingValidation.Request(value.Deserialize<StaffingSolveRequest>(ContractJson.Options)!, path, errors);
            return;
        }
        if (definition == "StaffingSolveResult")
        {
            var result = value.Deserialize<StaffingSolveResult>(ContractJson.Options)!;
            StaffingValidation.Request(result.Constraints, path + "/constraints", errors);
            if (errors.Count == 0 && !StaffingValidation.Valid(result.Constraints, result)) errors.Add(Error(path, "invalid_result"));
            return;
        }
        if (definition is "KpiSimulationRequest" or "KpiScenarioSnapshot")
        {
            ValidateSemantics(definition.Replace("Kpi", "SimPy"), value, path, errors);
            var interval = value.GetProperty("snapshotIntervalMinutes").GetDouble();
            if ((Math.Ceiling(value.GetProperty("durationMinutes").GetDouble() / interval) + 1) * value.GetProperty("checkpoints").GetArrayLength() > KpiConventions.MaxSnapshots)
                errors.Add(Error(path + "/snapshotIntervalMinutes", "out_of_range"));
            CheckInContractValidation.Request(value, path, errors);
            JourneyContractValidation.Request(value, path, errors);
            StaffScheduleContractValidation.Request(value, path, errors);
            if (errors.Count == 0) EventContractValidation.Validate(value, path, errors);
            return;
        }
        if (definition == "JourneySimulationResponse")
        {
            ValidateSemantics("SimulationResponse", value, path, errors);
            ValidateSemantics("KpiSimulationResponse", value.GetProperty("journeyResult").GetProperty("upstreamResult"), path + "/journeyResult/upstreamResult", errors);
            return;
        }
        if (definition == "KpiSimulationResponse")
        {
            ValidateSemantics("SimPySimulationResponse", value, path, errors);
            KpiContractValidation.Result(value.GetProperty("kpiResult"), path + "/kpiResult", errors);
            if (value.TryGetProperty("checkInResult", out var checkIn)) CheckInContractValidation.Result(checkIn, path + "/checkInResult", errors);
            return;
        }
        if (definition == "KpiResult") { KpiContractValidation.Result(value, path, errors); return; }
        if (definition == "KpiSnapshot") { KpiContractValidation.Snapshot(value.Deserialize<KpiSnapshot>(ContractJson.Options)!, path, errors); return; }
        if (definition == "KpiProcessResult") { ProcessContractValidation.Validate(value, path, errors); return; }
        if (definition is "SimPySimulationRequest" or "SimPyScenarioSnapshot")
        {
            ValidateSemantics(definition.Replace("SimPy", ""), value, path, errors);
            if (value.GetProperty("passengerCount").GetDouble() > 0 && value.GetProperty("arrivalRatePerMinute").GetDouble() == 0)
                errors.Add(Error(path + "/arrivalRatePerMinute", "out_of_range"));
            if (value.TryGetProperty("navigation", out _))
            {
                if (!value.TryGetProperty("passengerGeneration", out _)) errors.Add(Error(path + "/passengerGeneration", "required"));
                else NavigationContractValidation.Envelope(value, path, errors);
            }
            return;
        }
        if (definition == "SimPySimulationResponse")
        {
            ValidateSemantics(value.TryGetProperty("navigationResult", out _) ? "RoutedSimulationResponse" : "SimulationResponse", value, path, errors);
            ProcessContractValidation.Validate(value.GetProperty("processResult"), path + "/processResult", errors);
            return;
        }
        if (definition == "ProcessResult") { ProcessContractValidation.Validate(value, path, errors); return; }
        if (definition is "NavigationConfig" or "NavigationInput") { NavigationContractValidation.Validate(value,path,errors); return; }
        if (definition == "NavigationPreviewRequest") { NavigationContractValidation.Envelope(value,path,errors); return; }
        if (definition == "NavigationResult") { NavigationContractValidation.Result(value,path,errors); return; }
        if (definition is "RoutedSimulationRequest" or "RoutedScenarioSnapshot")
        {
            NavigationContractValidation.Envelope(value,path,errors);
            definition = definition.Replace("Routed","Profiled");
        }
        if (definition == "RoutedSimulationResponse")
        {
            NavigationContractValidation.Result(value.GetProperty("navigationResult"),path+"/navigationResult",errors);
            definition = "SimulationResponse";
        }
        if (definition == "PassengerGenerationRequest")
        {
            PassengerContractValidation.Validate(value, path, errors);
            return;
        }
        if (definition == "SimulationResponse")
        {
            var simulated = value.GetProperty("simulatedMinutes").GetDouble();
            if (value.GetProperty("status").GetString() == "completed" && simulated == 0)
                errors.Add(Error(path + "/simulatedMinutes", "invalid_result"));
            var snapshotIndex = 0;
            foreach (var snapshot in value.GetProperty("snapshots").EnumerateArray())
            {
                if (snapshot.GetProperty("elapsedMinutes").GetDouble() > simulated)
                    errors.Add(Error(path + "/snapshots/" + snapshotIndex + "/elapsedMinutes", "invalid_result"));
                snapshotIndex++;
            }
        }
        definition = definition switch
        {
            "SimulationResponse" => "SimulationResult", "OptimizationResponse" => "OptimizationResult",
            "ProfiledSimulationRequest" => "SimulationRequest", "ProfiledScenarioSnapshot" => "ScenarioSnapshot", "ProfiledOptimizationRequest" => "OptimizationRequest",
            _ => definition
        };
        void Add(string field, string code) => errors.Add(Error(path + field, code));
        switch (definition)
        {
            case "ScenarioSnapshot":
            case "SimulationRequest":
                if (definition == "ScenarioSnapshot" && !Utc(value.GetProperty("capturedAtUtc").GetString()!, out _))
                    Add("/capturedAtUtc", "invalid_format");
                var starts = value.GetProperty("startsAtUtc").GetString()!;
                if (!Utc(starts, out var timestamp) || DateTime.MaxValue - timestamp < TimeSpan.FromMinutes(value.GetProperty("durationMinutes").GetDouble()))
                    Add("/startsAtUtc", "invalid_format");
                var zone = value.GetProperty("timeZone").GetString()!;
                if (zone != "UTC" && (!zone.Contains('/') || !TimeZoneInfo.TryFindSystemTimeZoneById(zone, out _))) Add("/timeZone", "invalid_timezone");
                Unique(value.GetProperty("checkpoints"), "checkpointId", path + "/checkpoints", errors);
                if (value.TryGetProperty("passengerGeneration", out var generation))
                {
                    PassengerContractValidation.Validate(generation, path + "/passengerGeneration", errors);
                    foreach (var field in new[] { "seed", "durationMinutes" })
                        if (!Equivalent(generation.GetProperty(field), value.GetProperty(field))) Add("/passengerGeneration/" + field, "comparison_mismatch");
                    if (generation.GetProperty("batches").EnumerateArray().Sum(batch => batch.GetProperty("modeledCount").GetDouble()) != value.GetProperty("passengerCount").GetDouble())
                        Add("/passengerCount", "comparison_mismatch");
                    var outer = value.GetProperty("checkpoints").EnumerateArray().OrderBy(item => item.GetProperty("checkpointId").GetString()).ToArray();
                    var inner = generation.GetProperty("checkpoints").EnumerateArray().OrderBy(item => item.GetProperty("checkpointId").GetString()).ToArray();
                    if (!outer.SequenceEqual(inner, new CheckpointComparer())) Add("/passengerGeneration/checkpoints", "comparison_mismatch");
                }
                break;
            case "OptimizationRequest":
                var baseline = value.GetProperty("baseline");
                ValidateSemantics("SimulationRequest", baseline, path + "/baseline", errors);
                var seen = new HashSet<string> { baseline.GetProperty("runId").GetString()! };
                var index = 0;
                foreach (var candidate in value.GetProperty("candidates").EnumerateArray())
                {
                    var candidatePath = path + "/candidates/" + index++;
                    ValidateSemantics("SimulationRequest", candidate, candidatePath, errors);
                    if (!seen.Add(candidate.GetProperty("runId").GetString()!)) errors.Add(Error(candidatePath + "/runId", "duplicate_id"));
                    foreach (var field in new[] { "layoutVersionId", "startsAtUtc", "timeZone", "durationMinutes", "seed", "passengerCount", "arrivalRatePerMinute" })
                        if (!(field == "startsAtUtc"
                            ? Utc(baseline.GetProperty(field).GetString()!, out var baselineTime)
                                && Utc(candidate.GetProperty(field).GetString()!, out var candidateTime) && baselineTime == candidateTime
                            : Equivalent(baseline.GetProperty(field), candidate.GetProperty(field))))
                            errors.Add(Error(candidatePath + "/" + field, "comparison_mismatch"));
                    var baselineGeneration = baseline.TryGetProperty("passengerGeneration", out var bg) ? bg : default;
                    var candidateGeneration = candidate.TryGetProperty("passengerGeneration", out var cg) ? cg : default;
                    if (!ComparableGeneration(baselineGeneration, candidateGeneration)) errors.Add(Error(candidatePath + "/passengerGeneration", "comparison_mismatch"));
                }
                break;
            case "SimulationResult":
            case "OptimizationResult":
                var completed = value.GetProperty("status").GetString() == "completed";
                var failure = value.GetProperty("failure");
                if (completed != (failure.ValueKind == JsonValueKind.Null)) Add("/failure", "invalid_result");
                if (failure.ValueKind == JsonValueKind.Object && string.IsNullOrWhiteSpace(failure.GetProperty("message").GetString())) Add("/failure/message", "invalid_value");
                if (failure.ValueKind == JsonValueKind.Object && failure.GetProperty("code").GetString()!.Contains('\n')) Add("/failure/code", "invalid_format");
                if (definition == "SimulationResult")
                {
                    if (!completed && value.GetProperty("metrics").GetArrayLength() != 0) Add("/metrics", "invalid_result");
                    if (!completed && value.GetProperty("snapshots").GetArrayLength() != 0) Add("/snapshots", "invalid_result");
                    var metricIndex = 0;
                    foreach (var metric in value.GetProperty("metrics").EnumerateArray())
                    {
                        if (!Utc(metric.GetProperty("recordedAtUtc").GetString()!, out _)) Add("/metrics/" + metricIndex + "/recordedAtUtc", "invalid_format");
                        metricIndex++;
                    }
                }
                else
                {
                    var scores = value.GetProperty("candidates");
                    var score = value.GetProperty("baselineScore");
                    var recommended = value.GetProperty("recommendedRunId");
                    if (completed)
                    {
                        if (score.ValueKind == JsonValueKind.Null) Add("/baselineScore", "invalid_result");
                        if (scores.GetArrayLength() == 0) Add("/candidates", "invalid_result");
                        Unique(scores, "runId", path + "/candidates", errors, value.GetProperty("baselineRunId").GetString());
                        var ranks = scores.EnumerateArray().Select(x => (int)x.GetProperty("rank").GetDouble()).Order().ToArray();
                        if (!ranks.SequenceEqual(Enumerable.Range(1, ranks.Length)))
                        {
                            Add("/candidates", "invalid_result");
                            var seenRanks = new HashSet<int>();
                            var candidateIndex = 0;
                            foreach (var candidate in scores.EnumerateArray())
                            {
                                var rank = (int)candidate.GetProperty("rank").GetDouble();
                                if (rank > ranks.Length || !seenRanks.Add(rank)) Add("/candidates/" + candidateIndex + "/rank", "invalid_result");
                                candidateIndex++;
                            }
                        }
                        var winner = scores.EnumerateArray().FirstOrDefault(x => x.GetProperty("rank").GetDouble() == 1);
                        if (recommended.ValueKind == JsonValueKind.Null || winner.ValueKind == JsonValueKind.Undefined
                            || recommended.GetString() != winner.GetProperty("runId").GetString()) Add("/recommendedRunId", "invalid_result");
                        var ordered = scores.EnumerateArray().OrderBy(x => x.GetProperty("rank").GetDouble()).Select(x => x.GetProperty("score").GetDouble()).ToArray();
                        var expected = value.GetProperty("objective").GetString() == "minimizeMeanWaitMinutes" ? ordered.Order().ToArray() : ordered.OrderDescending().ToArray();
                        if (!ordered.SequenceEqual(expected)) Add("/candidates", "invalid_result");
                    }
                    else
                    {
                        if (score.ValueKind != JsonValueKind.Null) Add("/baselineScore", "invalid_result");
                        if (scores.GetArrayLength() != 0) Add("/candidates", "invalid_result");
                        if (recommended.ValueKind != JsonValueKind.Null) Add("/recommendedRunId", "invalid_result");
                    }
                }
                break;
        }
    }
    private static bool Equivalent(JsonElement left, JsonElement right) => left.ValueKind == JsonValueKind.Number
        ? left.GetDouble() == right.GetDouble() : JsonElement.DeepEquals(left, right);
    private sealed class CheckpointComparer : IEqualityComparer<JsonElement>
    {
        public bool Equals(JsonElement left, JsonElement right) => left.GetProperty("checkpointId").GetString() == right.GetProperty("checkpointId").GetString()
            && left.GetProperty("staffCount").GetDouble() == right.GetProperty("staffCount").GetDouble()
            && left.GetProperty("serviceRatePerMinute").GetDouble() == right.GetProperty("serviceRatePerMinute").GetDouble();
        public int GetHashCode(JsonElement value) => value.GetProperty("checkpointId").GetString()!.GetHashCode();
    }
    private static bool ComparableGeneration(JsonElement left, JsonElement right)
    {
        if (left.ValueKind == JsonValueKind.Undefined || right.ValueKind == JsonValueKind.Undefined) return left.ValueKind == right.ValueKind;
        var a = JsonNode.Parse(left.GetRawText())!.AsObject(); var b = JsonNode.Parse(right.GetRawText())!.AsObject();
        a.Remove("checkpoints"); b.Remove("checkpoints");
        return JsonNode.DeepEquals(a, b);
    }
    private static void ValidateIntegers(string definition, JsonElement value, string path, List<ContractFieldError> errors)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject()) ValidateIntegers(definition, property.Value, path + "/" + Escape(property.Name), errors);
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var child in value.EnumerateArray()) ValidateIntegers(definition, child, path + "/" + index++, errors);
        }
        else if (value.ValueKind == JsonValueKind.Number && SchemaAt(definition, path)["type"]?.ToString() == "integer")
        {
            var text = value.GetRawText();
            var exponentIndex = text.IndexOfAny(['e', 'E']);
            var coefficient = exponentIndex < 0 ? text : text[..exponentIndex];
            if (!coefficient.Any(character => character is >= '1' and <= '9')) return;
            var exponent = exponentIndex < 0 ? 0 : int.Parse(text[(exponentIndex + 1)..], CultureInfo.InvariantCulture);
            var point = coefficient.IndexOf('.');
            var fractionalDigits = point < 0 ? 0 : coefficient.Length - point - 1;
            var digits = coefficient.Replace(".", "");
            var trailingZeros = digits.Length - digits.TrimEnd('0').Length;
            if ((long)fractionalDigits - exponent > trailingZeros) errors.Add(Error(path, "invalid_type"));
        }
    }
    private static void ValidateNumbers(JsonElement value, string path, List<ContractFieldError> errors)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject()) ValidateNumbers(property.Value, path + "/" + Escape(property.Name), errors);
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var child in value.EnumerateArray()) ValidateNumbers(child, path + "/" + index++, errors);
        }
        else if (value.ValueKind == JsonValueKind.Number)
        {
            var text = value.GetRawText();
            var exponent = text.IndexOfAny(['e', 'E']);
            var coefficient = exponent < 0 ? text : text[..exponent];
            if (!value.TryGetDouble(out var number) || !double.IsFinite(number)
                || (number == 0 && coefficient.Any(character => character is >= '1' and <= '9')))
                errors.Add(Error(path, "out_of_range"));
        }
    }
    private static void ValidateIdentifiers(JsonElement value, string path, List<ContractFieldError> errors)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
            {
                var field = path + "/" + Escape(property.Name);
                if (property.Name.EndsWith("Id", StringComparison.Ordinal) && property.Value.ValueKind == JsonValueKind.String)
                {
                    var text = property.Value.GetString()!;
                    if (!Guid.TryParseExact(text, "D", out var id) || id.ToString("D") != text) errors.Add(Error(field, "invalid_format"));
                }
                ValidateIdentifiers(property.Value, field, errors);
            }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var child in value.EnumerateArray()) ValidateIdentifiers(child, path + "/" + index++, errors);
        }
    }
    private static void Unique(JsonElement array, string idField, string path, List<ContractFieldError> errors, string? baseline = null)
    {
        var seen = new HashSet<string>();
        if (baseline is not null) seen.Add(baseline);
        var index = 0;
        foreach (var item in array.EnumerateArray())
            if (!seen.Add(item.GetProperty(idField).GetString()!)) errors.Add(Error(path + "/" + index++ + "/" + idField, "duplicate_id"));
            else index++;
    }

    public static IReadOnlyList<ContractFieldError> DuplicateFields(JsonElement value, string path = "")
    {
        var errors = new List<ContractFieldError>();
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>();
            foreach (var property in value.EnumerateObject())
            {
                var field = path + "/" + Escape(property.Name);
                if (!seen.Add(property.Name)) errors.Add(Error(field, "duplicate_field"));
                errors.AddRange(DuplicateFields(property.Value, field));
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray()) errors.AddRange(DuplicateFields(item, path + "/" + index++));
        }
        else if (value.ValueKind == JsonValueKind.String)
            _ = value.GetString(); // Decode every string so invalid UTF-8 or unpaired surrogates fail before schema evaluation.
        return errors.Distinct().OrderBy(x => x.Field, StringComparer.Ordinal).ToArray();
    }
}
