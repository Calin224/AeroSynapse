export interface KpiFrame {
  runId: string;
  zoneId: string;
  minute: number;
  windowStartMinute: number;
  populationCount: number;
  notArrivedPassengers: number;
  queuedPassengers: number;
  processingPassengers: number;
  processedPassengers: number;
  unreachablePassengers: number;
  waitSampleCount: number;
  meanWaitMinutes: number | null;
  medianWaitMinutes: number | null;
  p95WaitMinutes: number | null;
  throughputPerMinute: number | null;
  cumulativeThroughputPerMinute: number | null;
  maximumQueue: number;
  firstQueueMinute: number | null;
  utilization: number | null;
  cumulativeUtilization: number | null;
  boardingLateRisk: number | null;
  boardingAtRiskCount: number;
  boardingRiskPopulationCount: number;
}

export interface KpiResult {
  runId: string;
  horizonMinutes: number;
  intervalMinutes: number;
  populationCount: number;
  processedCount: number;
  unprocessedCount: number;
  arrivedCount: number;
  notArrivedCount: number;
  unreachableCount: number;
  matchedSnapshotCount: number;
  snapshotCount: number;
  returnedSnapshotCount: number;
  snapshots: KpiFrame[];
}
