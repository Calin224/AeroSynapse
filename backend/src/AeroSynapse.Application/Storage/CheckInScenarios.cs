using AeroSynapse.Application.Contracts;
using AeroSynapse.Domain.DigitalTwin;

namespace AeroSynapse.Application.Storage;

/// <summary>Scenario check-in: staffed desks of a layout check-in area and the share of passengers with hold baggage.</summary>
public static class CheckInScenarios
{
    public static void Validate(ScenarioInputs inputs, SceneDetails layout)
    {
        if (inputs.CheckIn is not { } checkIn) return;
        var errors = new Dictionary<string, string[]>();
        var area = layout.CheckInAreas?.SingleOrDefault(item => item.Id == checkIn.AreaId);
        if (area is null) errors["inputs.checkIn.areaId"] = ["Check-in area is not present in the selected layout version."];
        else if (checkIn.StaffCount < 0 || checkIn.StaffCount > area.DeskCount)
            errors["inputs.checkIn.staffCount"] = [$"Staff between 0 and the area's {area.DeskCount} desks."];
        if (checkIn.HoldBaggageShare is < 0 or > 1) errors["inputs.checkIn.holdBaggageShare"] = ["Must be between 0 and 1."];
        if (inputs.PassengerGeneration?.Flow is not null)
            errors["inputs.checkIn"] = ["Check-in is not supported with alternative passenger flows."];
        if (errors.Count != 0) throw new ScenarioValidationException(errors);
    }

    // Older captures keep their engine semantics; only the KPI engine has check-in.
    public static CheckInInput? Input(ScenarioInputs inputs, SceneDetails layout, string? engine)
    {
        if (inputs.CheckIn is not { } checkIn) return null;
        if (engine != KpiConventions.Engine)
            throw new ScenarioValidationException(new Dictionary<string, string[]> { ["inputs.checkIn"] = ["Check-in needs the KPI engine. Launch the scenario again."] });
        var area = layout.CheckInAreas?.SingleOrDefault(item => item.Id == checkIn.AreaId)
            ?? throw new ScenarioValidationException(new Dictionary<string, string[]> { ["inputs.checkIn.areaId"] = ["Check-in area is not present in the selected layout version."] });
        return new(area.Id, checkIn.StaffCount, 60d / area.ServiceSeconds, (double)checkIn.HoldBaggageShare, area.WalkingSeconds / 60d);
    }
}
