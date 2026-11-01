using System.Text.Json;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Domain.DigitalTwin;

namespace AeroSynapse.Application.Storage;

public static class IntegratedResourceScenarios
{
    public static string? IncompatibilityReason(ScenarioInputs inputs)
    {
        var flights = inputs.Flights.Where(flight => flight.Status != "cancelled").ToArray();
        if (flights.Length is < 1 or > 50) return "Integrated planning requires 1–50 non-cancelled flights.";
        if (inputs.Events.Any(item => item.Type == "flight_change"))
            return "Integrated planning supports a fixed schedule. Remove flight_change events or disable integrated resources; scheduled flight delays remain supported.";
        foreach (var original in flights)
        {
            var flight = FlightScheduling.Resolve(original, inputs.StartsAtUtc);
            if (flight.BoardingOpensAtUtc is not { } open || !FlightScheduling.TryDelayed(open, flight.DelayMinutes, out var start)
                || !FlightScheduling.TryDelayed(flight.DepartureAtUtc, flight.DelayMinutes, out var end)
                || start < inputs.StartsAtUtc || end <= start || (end - inputs.StartsAtUtc).TotalMinutes > inputs.DurationMinutes)
                return $"Flight {flight.Code}'s full effective gate interval must fall inside the scenario horizon for integrated planning.";
        }
        return null;
    }

    public static ResourceSolveRequest Build(ScenarioDetails scenario, SceneDetails layout, double timeLimitSeconds)
    {
        Validate(scenario.Inputs, layout);
        var resources = scenario.Inputs.IntegratedResources ?? throw Invalid("", "Configure integrated resources before requesting a proposal.");
        var inputs = scenario.Inputs;
        var flights = inputs.Flights.Where(flight => flight.Status != "cancelled").Select(flight => FlightScheduling.Resolve(flight, inputs.StartsAtUtc))
            .Select(flight => new ResourceFlight(flight.Id, flight.GateId,
                (flight.BoardingOpensAtUtc!.Value.AddMinutes(flight.DelayMinutes) - inputs.StartsAtUtc).TotalMinutes,
                (flight.DepartureAtUtc.AddMinutes(flight.DelayMinutes) - inputs.StartsAtUtc).TotalMinutes)).ToArray();
        // Flights added after the resources were saved may use any configured gate of their terminal; removed flights drop out.
        var requirements = resources.Requirements.ToDictionary(item => item.FlightId);
        var terminals = layout.Gates.ToDictionary(gate => gate.Id, gate => gate.TerminalId);
        resources = resources with { Requirements = flights.Select(flight => requirements.GetValueOrDefault(flight.FlightId)
            ?? new ResourceRequirement(flight.FlightId, resources.Gates.Where(gate => terminals.GetValueOrDefault(gate.GateId) == terminals.GetValueOrDefault(flight.CurrentGateId))
                .Select(gate => gate.GateId).ToArray(), [], 1)).ToArray() };
        return ContractJson.Read<ResourceSolveRequest>(JsonSerializer.SerializeToElement(new ResourceSolveRequest("1.0",
            scenario.Scenario.Id, scenario.Scenario.Revision, inputs.DurationMinutes, timeLimitSeconds, resources, flights), ContractJson.Options), "ResourceSolveRequest");
    }

    public static void Validate(ScenarioInputs inputs, SceneDetails layout)
    {
        if (inputs.IntegratedResources is not { } config) return;
        try { ContractJson.Read<IntegratedResourceConfig>(JsonSerializer.SerializeToElement(config, ContractJson.Options), "IntegratedResourceConfig"); }
        catch (ContractValidationException failure)
        {
            throw new ScenarioValidationException(failure.Errors.GroupBy(error => "inputs.integratedResources" + error.Field.Replace('/', '.'))
                .ToDictionary(group => group.Key, group => group.Select(error => error.Message).ToArray()));
        }
        if (IncompatibilityReason(inputs) is { } reason) throw Invalid("", reason);
        // Requirements are per-flight overrides, so editing the schedule never breaks saved resources.
        if (config.Requirements.Select(item => item.FlightId).Distinct().Count() != config.Requirements.Count)
            throw Invalid("requirements", "Provide at most one requirement per flight.");
        var gates = layout.Gates.ToDictionary(item => item.Id);
        var staff = layout.Staff.ToDictionary(item => item.Id);
        var security = inputs.StaffAllocations.Select(item => item.StaffMemberId)
            .Concat(inputs.StaffShifts?.Shifts.Select(item => item.StaffMemberId) ?? []).ToHashSet();
        if (config.Gates.Any(item => !gates.ContainsKey(item.GateId))) throw Invalid("gates", "Use gates from the captured layout.");
        if (config.Workers.Any(item => !staff.ContainsKey(item.StaffMemberId))) throw Invalid("workers", "Use staff from the captured layout.");
        foreach (var team in config.Teams)
        {
            if (team.MemberIds.Any(security.Contains)) throw Invalid("teams", $"Team {team.Name} includes staff already reserved for security in this scenario.");
            if (team.GateIds.Any(gate => team.MemberIds.Any(member => staff[member].TerminalId != gates[gate].TerminalId)))
                throw Invalid("teams", $"Team {team.Name} and its supported gates must belong to the same terminal.");
        }
        if (config.Gates.SelectMany(item => item.Availability).Concat(config.Workers.SelectMany(item => item.Shifts))
            .Any(window => window.EndMinute > inputs.DurationMinutes)) throw Invalid("", "Availability and shifts must fall within the scenario horizon.");
    }

    private static ScenarioValidationException Invalid(string field, string message) => new(new Dictionary<string, string[]>
        { ["inputs.integratedResources" + (field.Length == 0 ? "" : "." + field)] = [message] });
}
