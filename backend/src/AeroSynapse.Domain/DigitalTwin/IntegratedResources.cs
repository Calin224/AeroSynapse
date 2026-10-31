namespace AeroSynapse.Domain.DigitalTwin;

// Explicit opt-in planning data. Windows are offsets from the scenario start, with exclusive ends.
public sealed record ResourceWindow(double StartMinute, double EndMinute);
public sealed record ResourceGate(Guid GateId, IReadOnlyList<ResourceWindow> Availability);
public sealed record ResourceWorker(Guid StaffMemberId, IReadOnlyList<string> Skills, IReadOnlyList<ResourceWindow> Shifts);
public sealed record HandlingTeam(Guid TeamId, string Name, IReadOnlyList<Guid> MemberIds, IReadOnlyList<Guid> GateIds);
public sealed record ResourceRequirement(Guid FlightId, IReadOnlyList<Guid> CompatibleGateIds,
    IReadOnlyList<string> RequiredSkills, int MinimumTeamSize);
public sealed record IntegratedResourceConfig(string ContractVersion, IReadOnlyList<ResourceGate> Gates,
    IReadOnlyList<ResourceWorker> Workers, IReadOnlyList<HandlingTeam> Teams, IReadOnlyList<ResourceRequirement> Requirements);
