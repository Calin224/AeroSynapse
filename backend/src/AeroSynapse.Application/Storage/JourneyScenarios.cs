using System.Text.Json;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Domain.DigitalTwin;

namespace AeroSynapse.Application.Storage;

public static class JourneyScenarios
{
    public static JourneyInput? Input(ScenarioInputs inputs, SceneDetails layout)
    {
        if (inputs.Journey is not { } config) return null;
        if (inputs.PassengerGeneration is null || inputs.PassengerGeneration.Flow is not null)
            throw Invalid("Save passenger profiles without alternative flows before enabling the complete journey.");
        if (config.Flights is null || config.Connections is null || config.Flights.Any(f => f is null) || config.Connections.Any(c => c is null)
            || config.Flights.Select(f => f.FlightId).Distinct().Count() != config.Flights.Count
            || !config.Flights.Select(f => f.FlightId).ToHashSet().SetEquals(inputs.Flights.Select(f => f.Id)))
            throw Invalid("Configure exactly one arrival and passport rule for every scheduled flight.");
        var profiles = PassengerScenarios.Request(inputs, layout).Flights.ToDictionary(f => f.FlightId);
        var flights = inputs.Flights.Select(item =>
        {
            var f = FlightScheduling.Resolve(item, inputs.StartsAtUtc);
            var rule = config.Flights.Single(r => r.FlightId == f.Id);
            var gate = layout.Gates.Single(g => g.Id == f.GateId);
            var p = profiles[f.Id];
            var checkpoints = (p.CheckpointRoutes?.Select(r => r.CheckpointId) ?? []).Append(p.CheckpointId).Distinct().ToArray();
            // A new gate must be in the same terminal, large enough, and reachable from every serving checkpoint.
            var eligible = layout.Gates.Where(g => g.TerminalId == gate.TerminalId && g.Capacity >= f.PassengerCapacity
                && checkpoints.All(cp => layout.Routes.Any(r => r.FromCheckpointId == cp && r.ToGateId == g.Id))
                && (inputs.Navigation is null || inputs.Navigation.GateBindings.Any(b => b.ResourceId == g.Id)))
                .Select(g => g.Id).Order().ToArray();
            return new JourneyFlight(f.Id, f.GateId, rule.ArrivalMinute,
                (f.BoardingOpensAtUtc!.Value - inputs.StartsAtUtc).TotalMinutes,
                (f.BoardingClosesAtUtc!.Value - inputs.StartsAtUtc).TotalMinutes,
                (f.DepartureAtUtc - inputs.StartsAtUtc).TotalMinutes, f.DelayMinutes, f.PassengerCapacity,
                f.Status == "cancelled", rule.RequiresPassport, rule.PreviousFlightId, rule.RotationMinutes, eligible);
        }).ToArray();
        return new("1.0", config.PassportStaffCount, config.PassportServiceMinutes, config.BoardingStaffPerGate,
            config.BoardingServiceMinutes, config.GateChangeWalkingMinutes, config.RiskBufferMinutes, flights,
            config.Connections.Select(c => new JourneyConnection(c.Id, c.InboundFlightId, c.OutboundFlightId, c.PassengerCount,
                c.TransferMinutes, c.RequiresPassport)).ToArray(),
            layout.Routes.Select(r => new JourneyRoute(r.FromCheckpointId, r.ToGateId, r.TravelSeconds / 60d)).ToArray());
    }

    public static void Validate(ScenarioInputs inputs, SceneDetails layout)
    {
        if (Input(inputs, layout) is not { } journey) return;
        var errors = VersionedContract.Validate("JourneyInput", JsonSerializer.SerializeToElement(journey, ContractJson.Options)).ToList();
        if (errors.Count == 0)
        {
            var context = JsonSerializer.SerializeToElement(new { journey, passengerGeneration = PassengerScenarios.Request(inputs, layout),
                inputs.DurationMinutes, snapshotIntervalMinutes = inputs.SnapshotIntervalMinutes ?? 1, events = ScheduledEvents.Wire(inputs) ?? [] }, ContractJson.Options);
            JourneyContractValidation.Request(context, "", errors);
        }
        if (errors.Count != 0) throw new ScenarioValidationException(errors.GroupBy(e => "inputs.journey" + e.Field.Replace('/', '.'))
            .ToDictionary(g => g.Key, g => g.Select(e => e.Message).Distinct().ToArray()));
    }

    public static ScenarioInputs ReconcileFlights(ScenarioInputs inputs) => inputs.Journey is not { } config ? inputs : inputs with
    {
        Journey = config with { Flights = inputs.Flights.Select(f => config.Flights.FirstOrDefault(rule => rule.FlightId == f.Id)
            ?? new ScenarioJourneyFlight(f.Id, (FlightScheduling.Resolve(f, inputs.StartsAtUtc).BoardingOpensAtUtc!.Value - inputs.StartsAtUtc).TotalMinutes,
                false, null, 0)).ToArray() }
    };

    private static ScenarioValidationException Invalid(string message) => new(new Dictionary<string, string[]> { ["inputs.journey"] = [message] });
}
