namespace AeroSynapse.Domain.DigitalTwin;

// Synthetic turnaround arrivals and aircraft rotations extend the existing departure schedule.
// Connecting passengers are reserved from the outbound flight's modeled cohort.
public sealed record ScenarioJourney(int PassportStaffCount, double PassportServiceMinutes, int BoardingStaffPerGate,
    double BoardingServiceMinutes, double GateChangeWalkingMinutes, double RiskBufferMinutes,
    IReadOnlyList<ScenarioJourneyFlight> Flights, IReadOnlyList<ScenarioJourneyConnection> Connections);
public sealed record ScenarioJourneyFlight(Guid FlightId, double ArrivalMinute, bool RequiresPassport,
    Guid? PreviousFlightId, int RotationMinutes);
public sealed record ScenarioJourneyConnection(Guid Id, Guid InboundFlightId, Guid OutboundFlightId,
    int PassengerCount, double TransferMinutes, bool RequiresPassport);
