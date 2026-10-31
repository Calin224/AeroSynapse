using AeroSynapse.Domain.DigitalTwin;

namespace AeroSynapse.Application.Storage;

/// <summary>What the agent may change, checked against the scenario it acts in.</summary>
public static class AgentScenarios
{
    public static void Validate(ScenarioInputs inputs, SceneDetails layout)
    {
        if (inputs.Agent is not { } agent) return;
        var errors = new Dictionary<string, string[]>();
        var reserve = agent.ReserveOperators ?? 0;
        if (!agent.StaffMoves && !agent.CheckInDesks && !agent.GateChoices && reserve == 0)
            errors["inputs.agent"] = ["Choose at least one action the agent may take."];
        if (agent.DecisionIntervalMinutes is < 5 or > 120)
            errors["inputs.agent.decisionIntervalMinutes"] = ["The agent decides every 5 to 120 simulated minutes."];
        if (agent.TransferMinutes is < 0 or > 60)
            errors["inputs.agent.transferMinutes"] = ["Moving between controls takes 0 to 60 minutes."];
        if (reserve is < 0 or > 50)
            errors["inputs.agent.reserveOperators"] = ["The agent may call in 0 to 50 reserve operators."];
        if (reserve > 0 && inputs.StaffShifts is not null)
            errors["inputs.agent.reserveOperators"] = ["The agent cannot call in operators on a shift plan. Use fixed staffing."];
        if (agent.StaffMoves && inputs.StaffShifts is not null)
            errors["inputs.agent.staffMoves"] = ["The agent cannot move operators on a shift plan. Use fixed staffing."];
        if (agent.StaffMoves && layout.Checkpoints.Count < 2)
            errors["inputs.agent.staffMoves"] = ["Moving operators needs at least two controls."];
        if (agent.CheckInDesks && inputs.CheckIn is null)
            errors["inputs.agent.checkInDesks"] = ["Opening desks needs check-in in the scenario."];
        if (agent.GateChoices && inputs.Journey is null)
            errors["inputs.agent.gateChoices"] = ["Choosing gates needs passenger journeys in the scenario."];
        if (inputs.PassengerGeneration?.Flow is not null)
            errors["inputs.agent"] = ["The agent is not supported with alternative passenger flows."];
        if (errors.Count != 0) throw new ScenarioValidationException(errors);
    }
}
