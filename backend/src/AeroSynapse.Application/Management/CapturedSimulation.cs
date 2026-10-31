using System.Text.Json;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Storage;
namespace AeroSynapse.Application.Management;
public static class CapturedSimulation
{
    // New captures use SimPy; older snapshots retain their original engine version for replay/retry.
    public static SimulationRequest Request(Guid runId, RunInputSnapshot captured)
    {
        var inputs = captured.Scenario.Inputs;
        var demand = inputs.Populations.Sum(x => (long)x.Count);
        if (captured.Engine is not null && demand is < 0 or > 100000)
            throw new ScenarioValidationException(new Dictionary<string, string[]> { ["inputs.populations"] = ["SimPy execution supports 0 to 100000 passengers."] });
        if (captured.Engine is null && demand is < 1 or > int.MaxValue)
            throw new ScenarioValidationException(new Dictionary<string, string[]> { ["inputs.populations"] = ["Legacy execution requires a positive passenger count."] });
        // Events act only on captures made by the KPI engine; older captures keep their original semantics.
        var events = captured.Engine == KpiConventions.Engine ? ScheduledEvents.Wire(inputs) : null;
        if (events is not null && inputs.PassengerGeneration is null)
            throw new ScenarioValidationException(new Dictionary<string, string[]> { ["inputs.events"] = ["Scheduled events need passenger profiles. Save the passenger configuration first."] });
        if (events is not null && inputs.PassengerGeneration!.Flow is not null)
            throw new ScenarioValidationException(new Dictionary<string, string[]> { ["inputs.events"] = ["Scheduled events are not supported with alternative passenger flows."] });
        var checkpoints = captured.Layout.Layout.Checkpoints.Select(checkpoint => new CheckpointInput(checkpoint.Id,
            checkpoint.CapacityPerMinute, StaffShiftScenarios.StaffCount(inputs, checkpoint.Id))).ToArray();
        // A shift plan reaches the engine as staffing steps; the KPI report then needs an events list, possibly empty.
        var schedules = StaffShiftScenarios.Schedules(inputs, captured.Layout.Layout, captured.Engine);
        if (schedules is not null) events ??= [];
        var snapshot = new ScenarioSnapshot(VersionedContract.Version, captured.Scenario.Scenario.Id,
            captured.Scenario.Scenario.Revision, captured.Layout.Version.Id, new DateTimeOffset(captured.CapturedAtUtc),
            new DateTimeOffset(inputs.StartsAtUtc), inputs.TimeZone, inputs.DurationMinutes, inputs.Seed,
            (int)demand, (double)demand / inputs.DurationMinutes, checkpoints,
            inputs.PassengerGeneration is null && !IncidentScenarios.DerivesProfiles(inputs) ? null : PassengerScenarios.Request(inputs, captured.Layout.Layout),
            inputs.Navigation is null ? null : NavigationScenarios.Input(inputs,captured.Layout.Layout), captured.Engine, captured.SnapshotIntervalMinutes, events,
            CheckInScenarios.Input(inputs, captured.Layout.Layout, captured.Engine), schedules,
            Journey: JourneyScenarios.Input(inputs, captured.Layout.Layout));
        snapshot = ContractJson.Read<ScenarioSnapshot>(JsonSerializer.SerializeToElement(snapshot, ContractJson.Options), "ScenarioSnapshot");
        return snapshot.ToSimulationRequest(runId);
    }
}
