export type AlertLevel = 'warning' | 'critical' | 'resolved';
export interface NotificationEvidence {
  fromMinute: number;
  toMinute: number;
  peakQueuePassengers: number;
  triggerMinute: number | null;
  periodStartUtc: string;
  periodEndUtc: string;
  observationSource: string;
  formula: string;
}
export interface OperationalAlert {
  id: string;
  scenarioId: string;
  scenarioName: string;
  runId: string;
  zoneId: string;
  zoneName: string;
  source: string;
  warningThreshold: number;
  criticalThreshold: number;
  horizonMinutes: number;
  level: AlertLevel;
  revision: number;
  observations: number;
  firstObservedAtUtc: string;
  lastObservedAtUtc: string;
  simulationAtUtc: string;
  evidence: NotificationEvidence;
  assignee: string;
  acknowledgedAtUtc: string | null;
  acknowledgedBy: string;
  isSynthetic: boolean;
}
export interface AlertActivity {
  id: string;
  alertId: string;
  scenarioId: string;
  runId: string;
  zoneId: string;
  kind: string;
  actor: string;
  text: string;
  recordedAtUtc: string;
  alertRevision: number;
  evidence?: NotificationEvidence | null;
}
export interface NotificationPage<T> {
  items: T[];
  total: number;
  offset: number;
  limit: number;
}
export interface AlertDetails {
  alert: OperationalAlert;
  activity: AlertActivity[];
}
export interface NotificationReceipt {
  commandId: string;
  alertIds: string[];
  duplicates: number;
  staleObservations: number;
  recordedAtUtc: string;
}
export interface PublishQueueAlerts {
  commandId: string;
  scenarioId: string;
  runId: string;
  fromMinute: number;
  horizonMinutes: number;
  warningThreshold: number;
  criticalThreshold: number;
  useLatest: boolean;
}
export interface AlertAction {
  commandId: string;
  revision: number;
  action: 'acknowledge' | 'comment' | 'assign';
  actor: string;
  text: string;
}
export type NotificationCommand =
  | { kind: 'publish'; body: PublishQueueAlerts }
  | { kind: 'action'; alertId: string; body: AlertAction };
export const alertLevelLabel = (level: string) =>
  ({ warning: 'Avertizare', critical: 'Critică', resolved: 'Rezolvată' })[level] ??
  'Nivel necunoscut';
export const actionLabel = (action: AlertAction['action']) =>
  ({ acknowledge: 'Confirmare', comment: 'Comentariu', assign: 'Atribuire' })[action];
export const activityLabel = (kind: string) =>
  (
    ({
      created: 'Alertă creată',
      observed: 'Observație agregată',
      resolved: 'Rezolvată în prognoză',
      reopened: 'Alertă redeschisă',
      level_changed: 'Nivel schimbat',
      acknowledged: 'Confirmare înregistrată',
      commented: 'Comentariu',
      assigned: 'Atribuire actualizată',
    }) as Record<string, string>
  )[kind] ?? 'Activitate înregistrată';
