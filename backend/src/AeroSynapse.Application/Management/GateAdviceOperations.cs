using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Storage;
using MediatR;

namespace AeroSynapse.Application.Management;

public sealed record AdviseGatesQuery(Guid ScenarioId, int Revision, ReportedDelay Delay) : IRequest<GateAdvice>;

/// <summary>
/// Recommends the gate plan for a delay reported now, from the scenario's own complete-journey day: the same
/// flights, eligible gates and scheduled delays the next run would use, so applying the delay reproduces it.
/// </summary>
public sealed class GateAdviceOperations(IScenarioService scenarios, IGateAdvisor advisor) : IRequestHandler<AdviseGatesQuery, GateAdvice>
{
    public async Task<GateAdvice> Handle(AdviseGatesQuery query, CancellationToken token)
    {
        var scenario = await scenarios.GetAsync(query.ScenarioId, token) ?? throw new ScenarioNotFoundException("Scenario was not found.");
        if (query.Revision != scenario.Scenario.Revision)
            throw new ScenarioConflictException("Scenario has changed. Reload before asking for gate advice.");
        var layout = await scenarios.GetLayoutAsync(scenario.Scenario.SceneId, scenario.Scenario.LayoutVersionId, token)
            ?? throw new ScenarioNotFoundException("Layout was not found.");
        var inputs = scenario.Inputs;
        var journey = JourneyScenarios.Input(inputs, layout.Layout)
            ?? throw new ScenarioConflictException("Enable the complete journey before asking for gate advice.");
        var delay = query.Delay;
        var errors = new Dictionary<string, string[]>();
        if (delay.Minute < 0 || delay.Minute >= inputs.DurationMinutes)
            errors["minute"] = [$"Choose a minute between 0 and {inputs.DurationMinutes - 1}."];
        if (journey.Flights.FirstOrDefault(f => f.FlightId == delay.FlightId) is not { } flight)
            errors["flightId"] = ["Choose a scheduled flight of this scenario."];
        else if (flight.IsCancelled)
            errors["flightId"] = ["A cancelled flight cannot be delayed."];
        if (delay.DelayMinutes is < 1 or > 1440)
            errors["delayMinutes"] = ["The delay must be between 1 and 1440 minutes."];
        if (errors.Count != 0) throw new ScenarioValidationException(errors);
        var scheduled = (ScheduledEvents.Wire(inputs) ?? []).Where(e => e is { Type: "flight_change", FlightId: not null, DelayMinutes: not null })
            .Select(e => new GateAdviceEvent(e.EventId, e.FlightId!.Value, e.Minute, e.DelayMinutes!.Value)).ToArray();
        return await advisor.AdviseAsync(new("1.0", delay.Minute, journey.Flights, scheduled,
            new(delay.FlightId, delay.DelayMinutes)), token);
    }
}
