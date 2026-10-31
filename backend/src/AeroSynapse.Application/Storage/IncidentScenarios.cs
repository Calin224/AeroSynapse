using System.Text.Json;
using System.Text.Json.Serialization;
using AeroSynapse.Domain.DigitalTwin;

namespace AeroSynapse.Application.Storage;

public sealed record PassengerSurgePayload(string ContractVersion, double DurationMinutes, double Share, bool IsSynthetic);

/// <summary>Compile scheduled demo incidents to the existing versioned passenger model.</summary>
public static class IncidentScenarios
{
    public const string Prefix = "incident.";
    public const string PassengerSurge = Prefix + "passenger-surge";
    public const string Version = "1.0";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true, NumberHandling = JsonNumberHandling.Strict
    };
    public static bool HasIncidents(ScenarioInputs inputs) => inputs.Events.Any(e => e.Type?.StartsWith(Prefix, StringComparison.Ordinal) == true);
    // Incidents and check-in act on individual passengers, so an aggregate demo compiles default profiles for them.
    public static bool DerivesProfiles(ScenarioInputs inputs) => HasIncidents(inputs) || inputs.CheckIn is not null;

    // An incident or check-in can enable profiles for an aggregate demo, preserving its modeled counts.
    // Existing explicit profiles keep every original distribution/group/flow parameter.
    public static PassengerGenerationConfig BaseConfig(ScenarioInputs inputs, SceneDetails layout)
    {
        if (inputs.PassengerGeneration is { } configured) return configured;
        if (!DerivesProfiles(inputs)) throw Invalid("inputs.passengerGeneration", "Configure passenger generation first.");
        var defaults = PassengerScenarios.Defaults(inputs, layout);
        return defaults with { Batches = defaults.Batches.Select(batch =>
        {
            var flight = inputs.Flights.Single(f => f.Id == batch.FlightId);
            var count = inputs.Flows.Where(flow => flow.FlightId == flight.Id).Sum(flow => (long)flow.PassengerCount);
            if (count > int.MaxValue) throw Invalid("inputs.flows", "The modeled population exceeds the supported limit.");
            return batch with { ModeledCount = (int)count, CountMode = count == flight.PassengerCapacity ? "full" : "subset" };
        }).ToArray() };
    }

    public static PassengerGenerationConfig Apply(ScenarioInputs inputs, SceneDetails layout, PassengerGenerationConfig config)
    {
        if (!HasIncidents(inputs)) return config;
        var errors = new Dictionary<string, List<string>>();
        void Error(string field, string message)
        { if (!errors.TryGetValue(field, out var values)) errors[field] = values = []; values.Add(message); }
        var additions = new Dictionary<Guid, List<(PassengerDemandSurge Surge, string Field)>>();
        var ids = new HashSet<Guid>();
        foreach (var (incident, index) in inputs.Events.Select((value, index) => (value, index)))
        {
            var field = $"inputs.events[{index}]";
            if (incident.Id == Guid.Empty || !ids.Add(incident.Id)) Error(field + ".id", "Un ID de eveniment nevid și unic în scenariu este obligatoriu.");
            if (incident.Type?.StartsWith(Prefix, StringComparison.Ordinal) != true) continue;
            if (incident.Type != PassengerSurge) { Error(field + ".type", "Tip de incident nesuportat. Folosește incident.passenger-surge."); continue; }
            PassengerSurgePayload? payload;
            try
            {
                using var document = JsonDocument.Parse(incident.PayloadJson);
                if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != document.RootElement.EnumerateObject().Count())
                    throw new JsonException();
                payload = document.RootElement.Deserialize<PassengerSurgePayload>(Json);
                if (payload is null) throw new JsonException();
            }
            catch (Exception error) when (error is JsonException or ArgumentException)
            { Error(field + ".payloadJson", "Payload-ul necesită exact contractVersion, durationMinutes, share și isSynthetic, cu tipurile corecte și fără câmpuri duplicate."); continue; }
            if (payload.ContractVersion != Version) Error(field + ".payloadJson.contractVersion", "Versiunea suportată a incidentului este 1.0.");
            if (!payload.IsSynthetic) Error(field + ".payloadJson.isSynthetic", "Incidentul trebuie marcat explicit sintetic (DEMO).");
            if (!double.IsFinite(payload.DurationMinutes) || payload.DurationMinutes is <= 0 or > 10080)
                Error(field + ".payloadJson.durationMinutes", "Durata trebuie să fie pozitivă, finită și de maximum 10080 minute simulate.");
            if (!double.IsFinite(payload.Share) || payload.Share is <= 0 or > 1)
                Error(field + ".payloadJson.share", "Ponderea trebuie să fie mai mare decât 0 și cel mult 1.");
            if (incident.OccurredAtUtc.Kind != DateTimeKind.Utc) Error(field + ".occurredAtUtc", "Momentul incidentului trebuie exprimat în UTC.");
            var start = (incident.OccurredAtUtc - inputs.StartsAtUtc).TotalMinutes;
            var end = start + payload.DurationMinutes;
            if (start < 0 || !double.IsFinite(end) || end > inputs.DurationMinutes)
                Error(field + ".payloadJson.durationMinutes", "Întregul interval al incidentului trebuie să fie în orizontul scenariului.");
            var flight = inputs.Flights.FirstOrDefault(f => f.Id == incident.FlightId);
            var batch = config.Batches.FirstOrDefault(b => b.FlightId == incident.FlightId);
            if (flight is null) { Error(field + ".flightId", "Alege un zbor din scenariu pentru passenger surge."); continue; }
            if (flight.Status == "cancelled" || batch is null || batch.ModeledCount <= 0)
                Error(field + ".flightId", "Incidentul necesită un zbor activ cu pasageri modelați.");
            if (incident.GateId != flight.GateId) Error(field + ".gateId", "Poarta incidentului trebuie să fie poarta zborului.");
            if (batch is null) continue;
            if (incident.CheckpointId != batch.CheckpointId || !layout.Checkpoints.Any(c => c.Id == incident.CheckpointId)
                || !layout.Routes.Any(r => r.FromCheckpointId == incident.CheckpointId && r.ToGateId == flight.GateId))
                Error(field + ".checkpointId", "Checkpoint-ul trebuie să corespundă profilului de pasageri și unui traseu către poarta zborului.");
            var close = (FlightScheduling.Resolve(flight, inputs.StartsAtUtc).BoardingClosesAtUtc!.Value.AddMinutes(flight.DelayMinutes) - inputs.StartsAtUtc).TotalMinutes;
            if (start < batch.Arrival.Minimum || end > batch.Arrival.Maximum || end > close)
                Error(field + ".payloadJson.durationMinutes", $"Intervalul trebuie inclus în fereastra de sosire [{batch.Arrival.Minimum:G}, {Math.Min(close, batch.Arrival.Maximum):G}] minute.");
            if (!additions.TryGetValue(flight.Id, out var list)) additions[flight.Id] = list = [];
            list.Add((new(start, end, payload.Share), field));
        }
        foreach (var batch in config.Batches)
        {
            if (!additions.TryGetValue(batch.FlightId, out var list)) continue;
            if (batch.Surges.Sum(s => s.Share) + list.Sum(s => s.Surge.Share) > 1 + 1e-9)
                foreach (var item in list) Error(item.Field + ".payloadJson.share", "Suma ponderilor tuturor vârfurilor pentru acest zbor, inclusiv cele existente, nu poate depăși 1.");
            if (batch.Surges.Count + list.Count > 16)
                foreach (var item in list) Error(item.Field, "Sunt permise cel mult 16 vârfuri pentru un zbor.");
        }
        if (errors.Count > 0) throw new ScenarioValidationException(errors.ToDictionary(e => e.Key, e => e.Value.ToArray()));
        return config with { Batches = config.Batches.Select(batch => additions.TryGetValue(batch.FlightId, out var list)
            ? batch with { Surges = [.. batch.Surges, .. list.OrderBy(s => s.Surge.StartsMinute).ThenBy(s => s.Surge.EndsMinute).ThenBy(s => s.Surge.Share).Select(s => s.Surge)] } : batch).ToArray() };
    }
    private static ScenarioValidationException Invalid(string field, string message) => new(new Dictionary<string, string[]> { [field] = [message] });
}
