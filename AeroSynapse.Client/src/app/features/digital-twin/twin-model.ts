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
export const TWIN_SURFACES = [
  {
    id: 'terrazzo',
    label: 'Terrazzo',
    texture: 'materials/airport/terminal-terrazzo.jpg',
  },
  {
    id: 'marble',
    label: 'Marmură',
    texture: 'materials/airport/marble_01_Diffuse.jpg',
  },
  {
    id: 'oak',
    label: 'Stejar',
    texture: 'materials/airport/oak_veneer_01_Diffuse.jpg',
  },
  {
    id: 'plaster',
    label: 'Tencuială',
    texture: 'materials/airport/white_plaster_02_Diffuse.jpg',
  },
  {
    id: 'apron',
    label: 'Platformă',
    texture: 'materials/airport/terminal-apron.jpg',
  },
  {
    id: 'carpet',
    label: 'Mochetă',
    texture: 'materials/airport/terminal-carpet.jpg',
  },
] as const;
export type TwinSurface = (typeof TWIN_SURFACES)[number]['id'];
export interface TwinRenderer {
  readonly ready?: Promise<void>;
  setScene(scene: TwinScene): void;
  setDisplayMode?(mode: TwinDisplayMode): Promise<void>;
  setResources?(resources: TwinResources): void;
  applySurface(entityId: string, surface: TwinSurface): Promise<void>;
  resetSurface(entityId: string): void;
  getAppliedSurface(entityId: string): TwinSurface | null;
  setSelectionHandler?(handler: (selection: TwinSelection | null) => void): void;
  setAssetStatusHandler?(handler: (status: { loading: boolean; error?: string }) => void): void;
  getSelectableZones?(): TwinSelection[];
  selectEntity?(entityId: string): void;
  resetCamera(): void;
  dispose(): void;
}
