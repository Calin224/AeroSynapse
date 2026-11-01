using System.Text.Json;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Storage;
using MediatR;

namespace AeroSynapse.Application.Management;

public sealed record StaffingCheckpointChoice(Guid Id, Guid TerminalId, string Name, int CurrentStaff);
public sealed record StaffingSettings(Guid ScenarioId, int Revision, bool PassengerProfilesActive, int AvailableStaff,
    IReadOnlyList<StaffingPool> StaffPools, IReadOnlyList<StaffingCheckpointChoice> Checkpoints);
public sealed record StaffingProposalRequest(int Revision, int AvailableStaff, double TimeLimitSeconds, IReadOnlyList<StaffingConstraint> Checkpoints);
public sealed record GetStaffingSettingsQuery(Guid ScenarioId) : IRequest<StaffingSettings>;
public sealed record ProposeStaffingQuery(Guid ScenarioId, StaffingProposalRequest Value) : IRequest<StaffingSolveResult>;
public sealed record BuildStaffingModelQuery(Guid ScenarioId, StaffingProposalRequest Value) : IRequest<StaffingSolveRequest>;
public sealed record BuildShiftPlanQuery(Guid ScenarioId, StaffingProposalRequest Value, ShiftRules Rules, double TimeLimitSeconds) : IRequest<ShiftPlanRequest>;
public sealed record StaffingCount(Guid CheckpointId, int StaffCount);
public sealed record StaffingAllocationRequest(int Revision, IReadOnlyList<StaffingCount> Checkpoints);
public sealed record ApplyStaffingCommand(Guid ScenarioId, StaffingAllocationRequest Value) : IRequest<ScenarioDetails>;

public sealed class StaffingOperations(IScenarioService scenarios, IPassengerGenerator generator, IStaffingPlanner planner) :
    IRequestHandler<GetStaffingSettingsQuery, StaffingSettings>, IRequestHandler<ProposeStaffingQuery, StaffingSolveResult>,
    IRequestHandler<BuildStaffingModelQuery, StaffingSolveRequest>, IRequestHandler<BuildShiftPlanQuery, ShiftPlanRequest>,
    IRequestHandler<ApplyStaffingCommand, ScenarioDetails>
{
    public async Task<StaffingSettings> Handle(GetStaffingSettingsQuery query, CancellationToken token)
    {
        var scenario = await Require(query.ScenarioId, token); var layout = await Layout(scenario, token);
        return Settings(scenario, layout);
    }

    private static StaffingSettings Settings(ScenarioDetails scenario, PublishedLayout layout)
    {
        var terminals = layout.Layout.Checkpoints.Select(item => item.TerminalId).Concat(layout.Layout.Staff.Select(item => item.TerminalId)).Distinct().Order().ToArray();
        return new(scenario.Scenario.Id, scenario.Scenario.Revision,
            IncidentScenarios.ProfilesActive(scenario.Inputs), layout.Layout.Staff.Count,
            terminals.Select(id => new StaffingPool(id, layout.Layout.Staff.Count(member => member.TerminalId == id))).ToArray(),
            layout.Layout.Checkpoints.OrderBy(item => item.Code, StringComparer.Ordinal).Select(item => new StaffingCheckpointChoice(item.Id,
                item.TerminalId, item.Name, StaffShiftScenarios.StaffCount(scenario.Inputs, item.Id))).ToArray());
    }

    /// <summary>Staff every checkpoint with a fixed count, keeping current people first; a shift plan gives way to it.</summary>
    public async Task<ScenarioDetails> Handle(ApplyStaffingCommand command, CancellationToken token)
    {
        var value = command.Value;
        var scenario = await Require(command.ScenarioId, token);
        if (scenario.Scenario.DeletedAtUtc is not null) throw new ScenarioConflictException("Archived scenarios cannot change staffing.");
        if (value.Revision != scenario.Scenario.Revision) throw new ScenarioConflictException("Scenario has changed. Reload before changing staffing.");
        var layout = await Layout(scenario, token);
        var counts = value.Checkpoints ?? [];
        if (counts.Any(item => item is null || item.StaffCount < 0) || counts.Select(item => item.CheckpointId).Distinct().Count() != counts.Count
            || !counts.Select(item => item.CheckpointId).ToHashSet().SetEquals(layout.Layout.Checkpoints.Select(item => item.Id)))
            throw Invalid("checkpoints", "Provide one non-negative staff count for every checkpoint in the captured layout.");
        var inputs = scenario.Inputs with { StaffShifts = null, StaffAllocations = StaffShiftScenarios.Allocate(scenario.Inputs, layout.Layout,
            counts.ToDictionary(item => item.CheckpointId, item => item.StaffCount), "checkpoints") };
        var summary = scenario.Scenario;
        return await scenarios.UpdateAsync(command.ScenarioId, new(summary.Code, summary.Name, summary.Description,
            summary.SceneId, summary.LayoutVersionId, inputs, value.Revision), token);
    }

    public async Task<StaffingSolveResult> Handle(ProposeStaffingQuery query, CancellationToken token) =>
        await planner.SolveAsync(await Handle(new BuildStaffingModelQuery(query.ScenarioId, query.Value), token), token);

    /// <summary>The same constraints and sampled demand as the fixed model, per 15-minute slot, for every person of the captured staff.</summary>
    public async Task<ShiftPlanRequest> Handle(BuildShiftPlanQuery query, CancellationToken token)
    {
        var model = await Handle(new BuildStaffingModelQuery(query.ScenarioId, query.Value), token);
        var scenario = await Require(query.ScenarioId, token);
        if (scenario.Inputs.DurationMinutes < StaffShiftScenarios.MinimumDurationMinutes)
            throw Invalid("durationMinutes", $"Shift plans need a scenario of at least {StaffShiftScenarios.MinimumDurationMinutes} minutes.");
        var layout = await Layout(scenario, token);
        var forecast = await generator.GenerateAsync(PassengerScenarios.Request(scenario.Inputs, layout.Layout), 0, 1, token);
        var demand = forecast.CheckpointDemand.ToDictionary(item => item.CheckpointId);
        var constraints = query.Value.Checkpoints.ToDictionary(item => item.CheckpointId);
        var slots = (scenario.Inputs.DurationMinutes + StaffShiftScenarios.SlotMinutes - 1) / StaffShiftScenarios.SlotMinutes;
        var staff = layout.Layout.Staff.OrderBy(member => member.Id).Take(query.Value.AvailableStaff)
            .Select(member => new ShiftStaffMember(member.Id, member.TerminalId)).ToArray();
        var checkpoints = model.Checkpoints.Select(item =>
        {
            var constraint = constraints[item.CheckpointId];
            return new ShiftCheckpoint(item.CheckpointId, item.TerminalId, constraint.MinimumStaff, constraint.MaximumStaff, constraint.PhysicalLimit,
                constraint.RequiredOpen, constraint.Closed, item.CapacityMilli, demand[item.CheckpointId].ArrivalsPerQuarterHour!);
        }).ToArray();
        var request = new ShiftPlanRequest("1.0", query.ScenarioId, query.Value.Revision, StaffShiftScenarios.SlotMinutes, slots,
            query.TimeLimitSeconds, query.Rules, staff, checkpoints);
        return ContractJson.Read<ShiftPlanRequest>(JsonSerializer.SerializeToElement(request, ContractJson.Options), "ShiftPlanRequest");
    }

    public async Task<StaffingSolveRequest> Handle(BuildStaffingModelQuery query, CancellationToken token)
    {
        var value = query.Value;
        ContractJson.Read<StaffingProposalRequest>(JsonSerializer.SerializeToElement(value, ContractJson.Options), "StaffingProposalRequest");
        var scenario = await Require(query.ScenarioId, token);
        if (value.Revision != scenario.Scenario.Revision) throw new ScenarioConflictException("Scenario has changed. Reload before proposing staffing.");
        var layout = await Layout(scenario, token);
        var settings = Settings(scenario, layout);
        if (value.AvailableStaff > settings.AvailableStaff) throw Invalid("availableStaff", $"The captured layout provides only {settings.AvailableStaff} staff members.");
        if (value.Checkpoints.Select(item => item.CheckpointId).Distinct().Count() != value.Checkpoints.Count
            || !value.Checkpoints.Select(item => item.CheckpointId).ToHashSet().SetEquals(settings.Checkpoints.Select(item => item.Id)))
            throw Invalid("checkpoints", "Provide exactly one set of constraints for every checkpoint in the captured layout.");
        if (!settings.PassengerProfilesActive) throw Invalid("passengerGeneration", "Activate passenger profiles or configure a passenger-surge incident before requesting staffing proposals.");
        // Passenger generation owns demand and service sampling. A one-person page still returns whole-population demand statistics.
        var generation = PassengerScenarios.Request(scenario.Inputs, layout.Layout);
        var forecast = await generator.GenerateAsync(generation, 0, 1, token);
        var demand = forecast.CheckpointDemand.ToDictionary(item => item.CheckpointId);
        var resources = settings.Checkpoints.ToDictionary(item => item.Id);
        var checkpoints = value.Checkpoints.OrderBy(item => item.CheckpointId).Select(item =>
        {
            var resource = resources[item.CheckpointId]; var estimate = demand[item.CheckpointId];
            var rate = estimate.MeanServiceMinutes > 0 ? 1 / estimate.MeanServiceMinutes
                : layout.Layout.Checkpoints.Single(cp => cp.Id == item.CheckpointId).CapacityPerMinute;
            // Rounding capacity down never promises more than the sampled model estimates.
            var capacity = Math.Floor(rate * 1000);
            // Passenger generation allows 0.01 service units, two 0.1 multipliers and a rate of 1,000,000/min:
            // the smallest sampled service is 1e-10 minutes, or 1e13 milli-passengers/min.
            if (!double.IsFinite(capacity) || capacity is < 0 or > 10000000000000)
                throw Invalid("checkpoints", "Estimated per-operator capacity is outside the supported range.");
            return new StaffingCheckpoint(item.CheckpointId, item.MinimumStaff, item.MaximumStaff, item.PhysicalLimit,
                item.RequiredOpen, item.Closed, item.ChangeCost, resource.TerminalId, resource.CurrentStaff,
                checked(estimate.PeakArrivalsPerMinute * 1000), (long)capacity);
        }).ToArray();
        var request = new StaffingSolveRequest("1.0", query.ScenarioId, value.Revision, value.AvailableStaff, value.TimeLimitSeconds, settings.StaffPools, checkpoints);
        request = ContractJson.Read<StaffingSolveRequest>(JsonSerializer.SerializeToElement(request, ContractJson.Options), "StaffingSolveRequest");
        return request;
    }

    private async Task<ScenarioDetails> Require(Guid id, CancellationToken token) =>
        await scenarios.GetAsync(id, token) ?? throw new ScenarioNotFoundException("Scenario was not found.");
    private async Task<PublishedLayout> Layout(ScenarioDetails scenario, CancellationToken token) =>
        await scenarios.GetLayoutAsync(scenario.Scenario.SceneId, scenario.Scenario.LayoutVersionId, token)
            ?? throw new ScenarioNotFoundException("Captured layout was not found.");
    private static ScenarioValidationException Invalid(string field, string message) => new(new Dictionary<string, string[]> { [field] = [message] });
}
