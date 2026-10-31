using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Domain.DigitalTwin;

namespace AeroSynapse.Application.Storage;

public static class PassengerScenarios
{
    // Fixed batches: incident-only scenarios compile these on every capture and retry, so they must not change.
    public static PassengerGenerationConfig Defaults(ScenarioInputs inputs, SceneDetails layout) => new("1.0",
        inputs.Flights.Select(flight => DefaultBatch(flight, inputs, layout)).ToArray());

    // What a new explicit configuration starts from: every flight uses all open checkpoints toward its gate.
    public static PassengerGenerationConfig SpreadDefaults(ScenarioInputs inputs, SceneDetails layout) => Spread(Defaults(inputs, layout));

    public static PassengerGenerationConfig Spread(PassengerGenerationConfig config) =>
        config with { Batches = config.Batches.Select(batch => batch with { CheckpointMode = PassengerBatchConfig.Spread }).ToArray() };

    private static PassengerBatchConfig DefaultBatch(ScenarioFlight flight, ScenarioInputs inputs, SceneDetails layout)
    {
        var routeId = inputs.Flows.FirstOrDefault(flow => flow.FlightId == flight.Id)?.RouteId;
        var route = layout.Routes.Where(route => route.ToGateId == flight.GateId)
            .OrderBy(route => route.Id != routeId).ThenBy(route => route.Code, StringComparer.Ordinal).FirstOrDefault()
            ?? throw Invalid("batches", $"Flight {flight.Code} has no route from a checkpoint to its gate.");
        var resolved = FlightScheduling.Resolve(flight, inputs.StartsAtUtc);
        var end = Math.Max(0, (resolved.BoardingClosesAtUtc!.Value.AddMinutes(flight.DelayMinutes) - inputs.StartsAtUtc).TotalMinutes);
        var excluded = flight.Status == "cancelled" || end == 0;
        return new(flight.Id, route.FromCheckpointId, excluded ? "subset" : "full", excluded ? 0 : flight.PassengerCapacity,
            new(end == 0 ? "constant" : "uniform", 0, end, end / 2, end / 6, end / 2),
            new("exponential", .05, 10, 1, 1, 1), new("uniform", .8, 1.3, 1, .1, 1),
            [new("solo", .65, 1, 1, 1, 1), new("family", .25, 2, 4, 1.2, 1.15), new("business", .1, 1, 1, .8, .9)], []);
    }

    public static PassengerGenerationRequest Request(ScenarioInputs inputs, SceneDetails layout)
    {
        var config = IncidentScenarios.BaseConfig(inputs, layout);
        ValidateShape(config);
        config = IncidentScenarios.Apply(inputs, layout, config);
        var flights = new List<PassengerFlightInput>();
        foreach (var flight in inputs.Flights)
        {
            var batch = config.Batches.FirstOrDefault(batch => batch.FlightId == flight.Id)
                ?? throw Invalid("batches", $"A batch is required for every flight, including {flight.Code}.");
            var route = layout.Routes.SingleOrDefault(route => route.ToGateId == flight.GateId && route.FromCheckpointId == batch.CheckpointId)
                ?? throw Invalid("batches", $"No route connects batch checkpoint {batch.CheckpointId} to flight {flight.Code}'s gate.");
            var resolved = FlightScheduling.Resolve(flight, inputs.StartsAtUtc);
            var close = (resolved.BoardingClosesAtUtc!.Value.AddMinutes(flight.DelayMinutes) - inputs.StartsAtUtc).TotalMinutes;
            // A spread batch offers every checkpoint routed to the gate; the generator keeps the staffed ones.
            var alternatives = batch.CheckpointMode == PassengerBatchConfig.Spread
                ? layout.Routes.Where(item => item.ToGateId == flight.GateId && layout.Checkpoints.Any(c => c.Id == item.FromCheckpointId))
                    .OrderBy(item => item.FromCheckpointId).Select(item => new PassengerCheckpointRoute(item.FromCheckpointId, item.Id, item.TravelSeconds / 60d)).ToArray()
                : null;
            flights.Add(new(flight.Id, flight.PassengerCapacity, Math.Max(0, close), flight.Status == "cancelled",
                batch.CheckpointId, route.Id, route.TravelSeconds / 60d, alternatives));
        }
        var checkpoints = layout.Checkpoints.Select(checkpoint => new CheckpointInput(checkpoint.Id,
            checkpoint.CapacityPerMinute, StaffShiftScenarios.StaffCount(inputs, checkpoint.Id))).ToArray();
        var request = new PassengerGenerationRequest(config.GeneratorVersion, inputs.Seed, inputs.DurationMinutes, checkpoints, flights, config.Batches, Flow: config.Flow);
        try { return ContractJson.Read<PassengerGenerationRequest>(JsonSerializer.SerializeToElement(request, ContractJson.Options), "PassengerGenerationRequest"); }
        catch (ContractValidationException exception) { throw Fields(exception); }
    }

    public static void Validate(ScenarioInputs inputs, SceneDetails layout)
    {
        var request = Request(inputs, layout);
        if (inputs.Populations.Sum(population => (long)population.Count) != request.Batches.Sum(batch => (long)batch.ModeledCount))
            throw Invalid("batches", "Population totals must equal the modeled batch counts. Save the passenger generation configuration to synchronize them.");
        foreach (var batch in request.Batches)
        {
            var matching = inputs.Flows.Where(flow => flow.FlightId == batch.FlightId).ToArray();
            if (matching.Sum(flow => (long)flow.PassengerCount) != batch.ModeledCount || matching.Any(flow => layout.Routes.Single(route => route.Id == flow.RouteId).FromCheckpointId != batch.CheckpointId))
                throw Invalid("batches", $"Flight {batch.FlightId}'s flows must match the modeled count and checkpoint.");
        }
    }

    // Configuring generation owns the synthetic populations/flows; unrelated staff, events and flights remain in the aggregate.
    public static ScenarioInputs Synchronize(ScenarioInputs inputs, SceneDetails layout)
    {
        var request = Request(inputs, layout);
        var populations = new List<PassengerPopulation>(); var flows = new List<PassengerFlow>();
        foreach (var batch in request.Batches.Where(batch => batch.ModeledCount > 0))
        {
            var flight = inputs.Flights.Single(flight => flight.Id == batch.FlightId);
            var info = request.Flights.Single(info => info.FlightId == batch.FlightId);
            var populationId = StableId("population", flight.Id);
            var end = FlightScheduling.Resolve(flight, inputs.StartsAtUtc).BoardingClosesAtUtc!.Value.AddMinutes(flight.DelayMinutes);
            populations.Add(new(populationId, $"Pasageri demo · {flight.Code}", batch.ModeledCount));
            flows.Add(new(StableId("flow", flight.Id), populationId, flight.Id, info.RouteId, batch.ModeledCount, inputs.StartsAtUtc, end));
        }
        return inputs with { Populations = populations, Flows = flows };
    }

    public static ScenarioInputs ReconcileFlights(ScenarioInputs previous, ScenarioInputs updated, SceneDetails layout)
    {
        updated = JourneyScenarios.ReconcileFlights(updated);
        if (previous.PassengerGeneration is not { } config) return updated;
        if (updated.Flights.Count == 0)
            return updated with { PassengerGeneration = null, Navigation = null, Populations = [], Flows = [] };
        var batches = updated.Flights.Select(flight =>
        {
            var batch = config.Batches.SingleOrDefault(batch => batch.FlightId == flight.Id)
                ?? DefaultBatch(flight, updated, layout) with { CheckpointMode = PassengerBatchConfig.Spread };
            if (flight.Status == "cancelled") return batch with { CountMode = "subset", ModeledCount = 0 };
            return batch.CountMode == "full" ? batch with { ModeledCount = flight.PassengerCapacity } : batch;
        }).ToArray();
        return Synchronize(updated with { PassengerGeneration = config with { Batches = batches } }, layout);
    }

    private static Guid StableId(string purpose, Guid flightId) => new(SHA256.HashData(Encoding.UTF8.GetBytes($"us019/v1/{purpose}/{flightId}"))[..16]);
    private static void ValidateShape(PassengerGenerationConfig config)
    {
        try { ContractJson.Read<PassengerGenerationConfig>(JsonSerializer.SerializeToElement(config, ContractJson.Options), "PassengerGenerationConfig"); }
        catch (ContractValidationException exception) { throw Fields(exception); }
    }
    private static ScenarioValidationException Fields(ContractValidationException exception) => new(exception.Errors
        .GroupBy(error => "inputs.passengerGeneration" + error.Field.Replace('/', '.'))
        .ToDictionary(group => group.Key, group => group.Select(error => error.Message).ToArray()));
    private static ScenarioValidationException Invalid(string field, string message) => new(new Dictionary<string, string[]>
        { ["inputs.passengerGeneration" + (field.Length == 0 ? "" : "." + field)] = [message] });
}
