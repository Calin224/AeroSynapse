export interface TwinAircraft {
  id: string;
  x: number;
  z: number;
  heading: number;
}
export interface TwinScene {
  aircraft: TwinAircraft[];
}
export type TwinDisplayMode = 'interior' | 'exterior';
export type TwinResource = 'aircraft' | 'baggage' | 'vehicles' | 'runway';
export type TwinResources = Record<TwinResource, boolean>;
export interface TwinSelection {
  entityId: string;
  zoneId: string;
  zoneType: 'terminal' | 'checkpoint' | 'gate' | 'scene';
  label: string;
  synthetic: true;
  resource: string;
}
export interface TwinRenderer {
  readonly ready?: Promise<void>;
  setScene(scene: TwinScene): void;
  setDisplayMode?(mode: TwinDisplayMode): Promise<void>;
  setResources?(resources: TwinResources): void;
  setSelectionHandler?(handler: (selection: TwinSelection | null) => void): void;
  setAssetStatusHandler?(handler: (status: { loading: boolean; error?: string }) => void): void;
  getSelectableZones?(): TwinSelection[];
  selectEntity?(entityId: string): void;
  resetCamera(): void;
  dispose(): void;
}
