export interface TwinAircraft {
  id: string;
  x: number;
  z: number;
  heading: number;
}
export interface TwinScene {
  aircraft: TwinAircraft[];
}
export interface TwinRenderer {
  setScene(scene: TwinScene): void;
  resetCamera(): void;
  dispose(): void;
}
