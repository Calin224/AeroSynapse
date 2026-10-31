namespace AeroSynapse.Domain.DigitalTwin;

// An AI agent acting inside simulation runs of this scenario. It never edits the scenario: each decision becomes an
// input of that run only, acting after the minute it was taken. Every DecisionIntervalMinutes it may move operators
// between controls or call in up to ReserveOperators extra operators (both serve TransferMinutes later), or open check-in
// desks; at each scheduled delay it chooses how the gates absorb it.
public sealed record ScenarioAgent(bool StaffMoves, bool CheckInDesks, bool GateChoices, int DecisionIntervalMinutes = 15, int TransferMinutes = 5,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] int? ReserveOperators = null)
{
    public const int DefaultReserveOperators = 10;

    /// <summary>
    /// The reserve the agent may call in. An agent that moves operators and was saved without one gets the default:
    /// it already decides on fixed staffing every interval, so only the options it is offered grow.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int Reserve => ReserveOperators ?? (StaffMoves ? DefaultReserveOperators : 0);
}

/// <summary>One decision the agent took in a run, kept with what it chose, why, and whether the model answered.</summary>
public sealed class AgentDecision
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public Guid ScenarioId { get; set; }
    public bool IsSynthetic { get; set; }
    public int Sequence { get; set; }
    public int Minute { get; set; }
    /// <summary>"periodic", or the flight_change event id for a gate decision.</summary>
    public string Point { get; set; } = "periodic";
    /// <summary>none, staff_move, staff_call, desk_open, plan, gate_choice or gate_open.</summary>
    public string Kind { get; set; } = "none";
    public string OptionId { get; set; } = "none";
    public string ActionJson { get; set; } = "{}";
    public string OptionsJson { get; set; } = "[]";
    public string ObservationJson { get; set; } = "{}";
    public string Reason { get; set; } = "";
    /// <summary>chosen, single_option, unconfigured, timeout, invalid_response, unavailable, quota_exhausted or budget_exhausted.</summary>
    public string ModelStatus { get; set; } = "chosen";
    public string Model { get; set; } = "";
    public long? InputTokens { get; set; }
    public long? OutputTokens { get; set; }
    public double? DurationMs { get; set; }
    public DateTime RecordedAtUtc { get; set; }
}
