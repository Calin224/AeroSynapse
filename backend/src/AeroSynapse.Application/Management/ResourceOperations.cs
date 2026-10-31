using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.DigitalTwin;
using MediatR;

namespace AeroSynapse.Application.Management;

public sealed record ResourceConfigWriteRequest(int Revision, IntegratedResourceConfig? Resources);
public sealed record ResourceProposalRequest(int Revision, double TimeLimitSeconds);
public sealed record ResourceStaffChoice(Guid Id, string Name, Guid TerminalId, bool ReservedForSecurity);
public sealed record ResourceGateChoice(Guid Id, string Name, Guid TerminalId);
public sealed record ResourceSettings(Guid ScenarioId, int Revision, DateTime StartsAtUtc, int DurationMinutes,
    bool Compatible, string? IncompatibilityReason, IntegratedResourceConfig? Resources,
    IReadOnlyList<ScenarioFlight> Flights, IReadOnlyList<ResourceGateChoice> Gates, IReadOnlyList<ResourceStaffChoice> Staff);
public sealed record GetResourceSettingsQuery(Guid ScenarioId) : IRequest<ResourceSettings>;
public sealed record WriteResourceConfigCommand(Guid ScenarioId, ResourceConfigWriteRequest Value) : IRequest<ScenarioDetails>;
public sealed record ProposeResourcesQuery(Guid ScenarioId, ResourceProposalRequest Value) : IRequest<ResourceSolveResult>;

public sealed class ResourceOperations(IScenarioService scenarios, IResourcePlanner planner) :
    IRequestHandler<GetResourceSettingsQuery, ResourceSettings>, IRequestHandler<WriteResourceConfigCommand, ScenarioDetails>,
    IRequestHandler<ProposeResourcesQuery, ResourceSolveResult>
{
    public async Task<ResourceSettings> Handle(GetResourceSettingsQuery query, CancellationToken token)
    {
        var scenario = await Require(query.ScenarioId, token); var layout = await Layout(scenario, token);
        var flights = scenario.Inputs.Flights.Where(item => item.Status != "cancelled").ToArray();
        var reason = IntegratedResourceScenarios.IncompatibilityReason(scenario.Inputs);
        var security = scenario.Inputs.StaffAllocations.Select(item => item.StaffMemberId).ToHashSet();
        return new(query.ScenarioId, scenario.Scenario.Revision, scenario.Inputs.StartsAtUtc, scenario.Inputs.DurationMinutes,
            reason is null, reason, scenario.Inputs.IntegratedResources, flights.Select(item => FlightScheduling.Resolve(item, scenario.Inputs.StartsAtUtc)).ToArray(),
            layout.Layout.Gates.Select(item => new ResourceGateChoice(item.Id, item.Name, item.TerminalId)).ToArray(),
            layout.Layout.Staff.Select(item => new ResourceStaffChoice(item.Id, item.DisplayName, item.TerminalId, security.Contains(item.Id))).ToArray());
    }

    public async Task<ScenarioDetails> Handle(WriteResourceConfigCommand command, CancellationToken token)
    {
        var scenario = await Require(command.ScenarioId, token);
        Revision(scenario, command.Value.Revision);
        return await scenarios.UpdateAsync(command.ScenarioId, new(scenario.Scenario.Code, scenario.Scenario.Name, scenario.Scenario.Description,
            scenario.Scenario.SceneId, scenario.Scenario.LayoutVersionId, scenario.Inputs with { IntegratedResources = command.Value.Resources }, command.Value.Revision), token);
    }

    public async Task<ResourceSolveResult> Handle(ProposeResourcesQuery query, CancellationToken token)
    {
        var scenario = await Require(query.ScenarioId, token); Revision(scenario, query.Value.Revision);
        var layout = await Layout(scenario, token);
        var request = IntegratedResourceScenarios.Build(scenario, layout.Layout, query.Value.TimeLimitSeconds);
        var result = await planner.SolveAsync(request, token);
        // Detect edits made while the solver was running before presenting the captured proposal.
        Revision(await Require(query.ScenarioId, token), query.Value.Revision);
        return result;
    }
    private static void Revision(ScenarioDetails scenario, int revision)
    {
        if (revision != scenario.Scenario.Revision) throw new ScenarioConflictException("Scenario has changed. Reload before planning integrated resources.");
    }
    private async Task<ScenarioDetails> Require(Guid id, CancellationToken token)
    {
        var scenario = await scenarios.GetAsync(id, token);
        return scenario is not null && scenario.Scenario.DeletedAtUtc is null ? scenario
            : throw new ScenarioNotFoundException("Scenario was not found.");
    }
    private async Task<PublishedLayout> Layout(ScenarioDetails scenario, CancellationToken token) =>
        await scenarios.GetLayoutAsync(scenario.Scenario.SceneId, scenario.Scenario.LayoutVersionId, token)
            ?? throw new ScenarioNotFoundException("Captured layout was not found.");
}
