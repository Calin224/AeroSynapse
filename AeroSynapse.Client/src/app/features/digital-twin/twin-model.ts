export interface TwinAircraft {
  id: string;
  x: number;
  z: number;
  heading: number;
}
export interface TwinScene {
  aircraft: TwinAircraft[];
  scenarioId?: string;
  gateCount?: number;
  checkpoints?: TwinCheckpoint[];
  heatmap?: HeatmapDataset;
  navigationRoutes?: TwinNavigationRoute[];
}
export interface TwinNavigationRoute {
  routeKey: string;
  passengerCount: number;
  waypoints: { zoneId: string; x: number; y: number; z: number }[];
}
export interface TwinCheckpoint {
  zoneId: string;
  code: string;
  capacityPerMinute: number;
  staff: { id: string; name: string; role: string }[];
  status: 'open' | 'busy' | 'closed';
  /** averageWaitMinutes is null when no passenger finished waiting at this checkpoint. */
  metrics: {
    queuedPassengers: number | null;
    processedPassengers: number | null;
    averageWaitMinutes: number | null;
  };
}
export type TwinNavigationMode = 'orbit' | 'pan';
export type TwinFailureReason = 'unavailable' | 'performance';
export interface TwinPerformance {
  fps: number;
  frameP95Ms: number;
  sampleFrames: number;
  sampleSeconds: number;
  pixelRatio: number;
  quality: 'high' | 'balanced' | 'low';
  state: 'ok' | 'adjusting' | 'low' | 'fallback';
}
export type TwinDisplayMode = 'interior' | 'exterior';
export type TwinCameraView = 'overview' | 'gates' | 'security' | 'immersive';
export type TwinResource = 'aircraft' | 'baggage' | 'vehicles' | 'runway' | 'stands' | 'apron';
export type TwinResources = Record<TwinResource, boolean>;
export const DEFAULT_TWIN_RESOURCES: TwinResources = {
  aircraft: true,
  baggage: true,
  vehicles: true,
  runway: false,
  stands: true,
  apron: true,
};
export type TwinDetailPolicy = 'auto' | 'low';
export interface TwinExteriorStatus {
  level: 'unloaded' | 'low' | 'detail';
  policy: TwinDetailPolicy;
  entities: number;
}
export interface TwinExteriorDetails {
  code: string;
  status: 'synthetic';
  positionMeters: [number, number, number];
  dimensionsMeters: [number, number, number];
  headingDegrees?: number;
  flightCode?: string;
  gateId?: string;
  gateCode?: string;
  level: 'low' | 'detail';
  positionSource: 'scenario-demo' | 'authored-layout';
}
export interface TwinSelection {
  entityId: string;
  zoneId: string;
  zoneType: 'terminal' | 'checkpoint' | 'checkIn' | 'gate' | 'scene';
  label: string;
  synthetic: true;
  resource: string;
  exterior?: TwinExteriorDetails;
}
export interface TwinRenderer {
  readonly ready?: Promise<void>;
  setScene(scene: TwinScene): void;
  setHeatmap?(frame: HeatmapFrame | null): void;
  setPassengers?(packet: PassengerPacket | null): void;
  setPassengerStatsHandler?(handler: (stats: PassengerStats) => void): void;
  setHoverHandler?(handler: (hover: TwinHover | null) => void): void;
  setDisplayMode?(mode: TwinDisplayMode): Promise<void>;
  setExteriorDetailPolicy?(policy: TwinDetailPolicy): void;
  setExteriorStatusHandler?(handler: (status: TwinExteriorStatus) => void): void;
  setCameraView?(view: TwinCameraView): void;
  setNavigationMode?(mode: TwinNavigationMode): void;
  zoomCamera?(direction: 'in' | 'out'): void;
  setActive?(active: boolean): void;
  setPerformanceHandler?(handler: (status: TwinPerformance) => void): void;
  setResources?(resources: TwinResources): void;
  setSelectionHandler?(handler: (selection: TwinSelection | null) => void): void;
  setAssetStatusHandler?(handler: (status: { loading: boolean; error?: string }) => void): void;
  getSelectableZones?(): TwinSelection[];
  selectEntity?(entityId: string): void;
  resetCamera(): void;
  dispose(): void;
}
import type { HeatmapDataset, HeatmapFrame, TwinHover } from './heatmap-model';
import type { PassengerPacket, PassengerStats } from './passenger-model';
