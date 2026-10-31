using System.Text.Json;

namespace AeroSynapse.Application.Storage;

public static class RunResultValidator
{
    public static void ObjectJson(string? value, string field)
    {
        var errors = new Errors();
        errors.ObjectJson(value, field);
        errors.Throw();
    }
    public static void Sample(RunInputSnapshot snapshot, DateTime time, string zoneType, Guid zoneId)
    {
        var errors = new Errors();
        errors.Utc(time, "recordedAtUtc");
        var inputs = snapshot.Scenario.Inputs;
        if (time < inputs.StartsAtUtc || time > inputs.StartsAtUtc.AddMinutes(inputs.DurationMinutes))
            errors.Add("recordedAtUtc", "Must fall within the captured simulation time window.");
        var layout = snapshot.Layout.Layout;
        var exists = zoneType switch
        {
            "scene" => zoneId == layout.Scene.Id,
            "terminal" => layout.Terminals.Any(x => x.Id == zoneId),
            "checkpoint" => layout.Checkpoints.Any(x => x.Id == zoneId),
            "gate" => layout.Gates.Any(x => x.Id == zoneId),
            "checkIn" => layout.CheckInAreas?.Any(x => x.Id == zoneId) == true,
            _ => false
        };
        if (!exists) errors.Add("zoneId", "Zone type and identifier must refer to a zone in the run's captured layout.");
        errors.Throw();
    }

    public static void Metric(RunMetricRequest request)
    {
        var errors = new Errors();
        errors.Text(request.Name, "name", 64);
        errors.Text(request.Unit, "unit", 32);
        errors.Number(request.Value, "value");
        errors.Throw();
    }

    public static void Snapshot(RunSnapshotRequest request)
    {
        var errors = new Errors();
        errors.ObjectJson(request.PayloadJson, "payloadJson");
        errors.Throw();
    }

    public static void Query(RunResultQuery query)
    {
        var errors = new Errors();
        if (query.Limit is < 1 or > 100) errors.Add("limit", "Must be between 1 and 100.");
        if (query.FromUtc.HasValue) errors.Utc(query.FromUtc.Value, "fromUtc");
        if (query.ToUtc.HasValue) errors.Utc(query.ToUtc.Value, "toUtc");
        if (query.FromUtc > query.ToUtc) errors.Add("toUtc", "Must not precede fromUtc.");
        if ((query.ZoneType is null) != (query.ZoneId is null)) errors.Add("zoneId", "Supply zoneType and zoneId together.");
        if (query.ZoneType is not null && query.ZoneType is not ("scene" or "terminal" or "checkpoint" or "gate" or "checkIn"))
            errors.Add("zoneType", "Must be scene, terminal, checkpoint, gate or checkIn.");
        if (query.ZoneId == Guid.Empty) errors.Add("zoneId", "A nonempty identifier is required.");
        errors.Throw();
    }

    public static void Recommendation(RecommendationRequest request)
    {
        var errors = new Errors();
        errors.Text(request.Objective, "objective", 160);
        errors.Text(request.Rationale, "rationale", 2000);
        errors.Number(request.BaselineScore, "baselineScore");
        if (request.BaselineRunId == Guid.Empty) errors.Add("baselineRunId", "A nonempty identifier is required.");
        if (request.Candidates is null || request.Candidates.Count is < 1 or > 100)
            errors.Add("candidates", "Supply between 1 and 100 candidates.");
        var runs = new HashSet<Guid>();
        var ranks = new HashSet<int>();
        foreach (var (candidate, index) in (request.Candidates ?? []).Select((value, index) => (value, index)))
        {
            var field = $"candidates[{index}]";
            if (candidate is null) { errors.Add(field, "A candidate is required."); continue; }
            if (candidate.RunId == Guid.Empty || candidate.RunId == request.BaselineRunId)
                errors.Add(field + ".runId", "Candidate must identify a run different from the baseline.");
            if (!runs.Add(candidate.RunId)) errors.Add(field + ".runId", "Candidate runs must be unique.");
            if (candidate.Rank <= 0 || !ranks.Add(candidate.Rank)) errors.Add(field + ".rank", "Ranks must be positive and unique.");
            errors.Number(candidate.Score, field + ".score");
            errors.ObjectJson(candidate.AssessmentJson, field + ".assessmentJson");
        }
        errors.Throw();
    }

    private sealed class Errors
    {
        private readonly Dictionary<string, List<string>> values = [];
        public void Add(string field, string message)
        {
            if (!values.TryGetValue(field, out var messages)) values[field] = messages = [];
            messages.Add(message);
        }
        public void Text(string? value, string field, int max)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > max) Add(field, $"A nonempty value of at most {max} characters is required.");
            else if (value.Contains('\0')) Add(field, "Must not contain NUL (U+0000) characters.");
        }
        public void Utc(DateTime value, string field)
        {
            if (value == default || value.Kind != DateTimeKind.Utc) Add(field, "A UTC timestamp with the Z suffix is required.");
        }
        public void Number(decimal value, string field)
        {
            if (value <= -1000000000000m || value >= 1000000000000m || decimal.Round(value, 6) != value)
                Add(field, "Must fit numeric(18,6) without rounding: at most 12 integer digits and 6 fractional digits.");
        }
        public void ObjectJson(string? value, string field)
        {
            try
            {
                using var document = JsonDocument.Parse(value ?? "null");
                if (document.RootElement.ValueKind != JsonValueKind.Object) Add(field, "Must contain a JSON object.");
                else if (ContainsNul(document.RootElement)) Add(field, "JSON strings and property names must not contain NUL (U+0000) characters.");
            }
            catch (JsonException) { Add(field, "Must contain valid JSON."); }
            catch (InvalidOperationException) { Add(field, "Must contain valid Unicode text."); }
        }
        private static bool ContainsNul(JsonElement element) => element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().Any(x => x.Name.Contains('\0') || ContainsNul(x.Value)),
            JsonValueKind.Array => element.EnumerateArray().Any(ContainsNul),
            JsonValueKind.String => element.GetString()!.Contains('\0'),
            _ => false
        };
        public void Throw()
        {
            if (values.Count > 0) throw new ScenarioValidationException(values.ToDictionary(x => x.Key, x => x.Value.ToArray()));
        }
    }
}
