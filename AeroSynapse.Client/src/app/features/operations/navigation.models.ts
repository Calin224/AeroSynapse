export interface NavigationNode {
  id: string; name: string; kind: string; x: number; y: number; z: number;
  isActive: boolean; isStepFree: boolean; requiredTags: string[];
}
export interface NavigationEdge {
  id: string; fromZoneId: string; toZoneId: string; distanceMeters: number; travelSeconds: number;
  isBidirectional: boolean; isActive: boolean; isStepFree: boolean; requiredTags: string[];
}
export interface NavigationConfig {
  navigationVersion: string; graph: { nodes: NavigationNode[]; edges: NavigationEdge[] };
  checkpointBindings: { resourceId: string; zoneId: string }[]; gateBindings: { resourceId: string; zoneId: string }[];
  accessProfiles: { groupKind: string; requireStepFree: boolean; accessTags: string[]; forbiddenZoneIds: string[]; walkingMultiplier: number }[];
  closedZoneIds: string[]; closedEdgeIds: string[];
}
export interface NavigationSettings {
  scenarioId: string; revision: number; passengerProfilesActive: boolean; config: NavigationConfig | null; defaults: NavigationConfig;
  checkpoints: { id: string; name: string }[]; gates: { id: string; name: string }[];
}
export interface NavigationRoute {
  routeKey: string; flightId: string; gateId: string; checkpointId: string; groupKind: string;
  zoneIds: string[]; edgeIds: string[]; distanceMeters: number; baseWalkingMinutes: number; passengerCount: number;
  waypoints: { zoneId: string; x: number; y: number; z: number }[];
}
export interface NavigationResult {
  navigationVersion: string; algorithm: string; graphFingerprint: string; populationFingerprint: string; isSynthetic: boolean;
  modeledCount: number; routedCount: number; errorCount: number; assessed: boolean; routes: NavigationRoute[];
  flights: { flightId: string; gateId: string; modeledCount: number; routedCount: number; errorCount: number;
    safeCount: number; lateCount: number; unservedCount: number; pendingCount: number; meanWalkingMinutes: number | null; meanQueueWaitMinutes: number | null }[];
  journeys: { id: string; flightId: string; gateId: string; checkpointId: string; routeKey: string | null;
    error: { code: string; message: string } | null; walkingMinutes: number | null; queueWaitMinutes: number | null;
    serviceMinutes: number; arrivalMinute: number; gateArrivalMinute: number | null; boardingCloseMinute: number; boardingRisk: string }[];
  offset: number; returnedCount: number; hasMore: boolean;
}
