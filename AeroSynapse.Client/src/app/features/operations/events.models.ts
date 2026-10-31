// Operational events. Listed in the engine's order for simultaneous events: same minute → this order → ID.
export const EVENT_TYPES = [
  { value: 'flight_change', label: 'Modificare zbor' },
  { value: 'corridor_closure', label: 'Închidere coridor' },
  { value: 'checkpoint_unavailable', label: 'Control indisponibil' },
  { value: 'staff_reduction', label: 'Scădere personal' },
  { value: 'resume', label: 'Reluare' },
  { value: 'passenger_surge', label: 'Val de pasageri' },
] as const;
export type EventType = (typeof EVENT_TYPES)[number]['value'];

export interface ScenarioEvent {
  id: string; type: string; occurredAtUtc: string;
  checkpointId?: string | null; gateId?: string | null; flightId?: string | null; payloadJson: string;
}

export function eventLabel(type: string): string {
  return EVENT_TYPES.find((item) => item.value === type)?.label ?? type;
}
export function isOperational(type: string): type is EventType {
  return EVENT_TYPES.some((item) => item.value === type);
}
export function priority(type: string): number {
  return EVENT_TYPES.findIndex((item) => item.value === type);
}
