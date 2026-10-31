export interface JourneyConfig {
  passportStaffCount: number;
  passportServiceMinutes: number;
  boardingStaffPerGate: number;
  boardingServiceMinutes: number;
  gateChangeWalkingMinutes: number;
  riskBufferMinutes: number;
  flights: {
    flightId: string;
    arrivalMinute: number;
    requiresPassport: boolean;
    previousFlightId: string | null;
    rotationMinutes: number;
  }[];
  connections: {
    id: string;
    inboundFlightId: string;
    outboundFlightId: string;
    passengerCount: number;
    transferMinutes: number;
    requiresPassport: boolean;
  }[];
}
