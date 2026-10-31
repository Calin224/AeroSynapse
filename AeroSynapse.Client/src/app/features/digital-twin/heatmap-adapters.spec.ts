import { appendProgress, capturedHeatmapRun, mergeHeatmapRuns } from './heatmap-adapters';
import { datasetFixture, storedFixture } from './heatmap.testing';
import { motionFixture } from './passenger.testing';
import { preparePassengerReplay } from './passenger-model';
import { RunDetails } from '../operations/operations.models';

describe('Captured run and live ZoneSnapshot adapters', () => {
  it('consumes the stored KPI series and excludes snapshots from a different run', () => {
    const saved = storedFixture(datasetFixture().baseline);
    const zoneId = datasetFixture().baseline.capacities[0].zoneId;
    const source = {
      ...saved,
      snapshots: [0, 1, 2].map((minute) => ({
        id: `snapshot-${minute}`,
        zoneId,
        recordedAtUtc: '2026-10-14T10:00:00Z',
        payloadJson: JSON.stringify({
          kpiVersion: '1.0',
          runId: minute === 2 ? 'other-run' : saved.id,
          zoneId,
          minute,
          elapsedMinutes: minute,
          queuedPassengers: Math.max(0, 1 - minute),
          processedPassengers: minute,
        }),
      })),
    };
    const timeline = capturedHeatmapRun(source)!;
    expect(timeline.snapshots.map((row) => row.elapsedMinutes)).toEqual([0, 1]);
  });
  it('uses the complete captured KPI cadence after completion instead of mixing it with progress frames', () => {
    const current = { ...datasetFixture().baseline, source: 'live-preview' as const };
    const captured = {
      ...current,
      status: 'completed',
      source: 'stored-run' as const,
      snapshotIntervalMinutes: 5,
      snapshots: current.snapshots.map((row, index) => ({ ...row, elapsedMinutes: index * 5 })),
    };
    expect(mergeHeatmapRuns(current, captured).snapshots).toEqual(captured.snapshots);
    expect(mergeHeatmapRuns(current, captured).source).toBe('stored-run');
    const delayedProgress = {
      runId: captured.runId,
      status: 'running',
      revision: 1,
      resultsValid: false,
      simulationMinute: 3,
      snapshot: {
        totalZones: 1,
        zones: [
          { zoneId: captured.capacities[0].zoneId, queuedPassengers: 25, processedPassengers: 10 },
        ],
      },
    };
    expect(appendProgress(captured, delayedProgress)).toBe(captured);
  });
  it('keeps observed progress for alternative flows with a captured KPI engine but only a final v1 snapshot', () => {
    const current = datasetFixture().baseline,
      saved = storedFixture(current);
    const input = JSON.parse(saved.inputSnapshotJson!);
    input.engine = 'simpy-kpi-1.0';
    input.snapshotIntervalMinutes = 5;
    input.scenario.inputs.passengerGeneration = {
      flow: { segments: [], stages: [], closures: [] },
    };
    const captured = capturedHeatmapRun({
      ...saved,
      inputSnapshotJson: JSON.stringify(input),
      snapshots: saved.snapshots.slice(-1),
    })!;
    expect(captured.snapshotIntervalMinutes).toBeUndefined();
    expect(mergeHeatmapRuns(current, captured).snapshots).toEqual(current.snapshots);
  });
  it('passes valid optional motion from stored runs through comparison adapters and ignores invalid results', () => {
    const source = motionFixture(),
      saved = { ...storedFixture(source), passengerMotion: source.passengerMotion };
    const captured = capturedHeatmapRun(saved)!;
    expect(preparePassengerReplay(captured).mode).toBe('individual');
    expect(
      mergeHeatmapRuns(captured, capturedHeatmapRun(storedFixture(source))!).passengerMotion,
    ).toBe(source.passengerMotion);
    expect(capturedHeatmapRun({ ...saved, resultsValid: false })!.passengerMotion).toBeUndefined();
  });
  it('sums captured flows per flight gate so playback can follow real gate demand', () => {
    const saved = storedFixture(datasetFixture().baseline);
    const input = JSON.parse(saved.inputSnapshotJson!);
    input.scenario.inputs.flights = [
      { id: 'f1', gateId: 'gate-1' },
      { id: 'f2', gateId: 'gate-1' },
      { id: 'f3', gateId: 'gate-3' },
      { id: 'f4', gateId: 'gate-4' },
    ];
    // f4 has no passengers: its gate must not attract anyone.
    input.scenario.inputs.flows = [
      { flightId: 'f1', passengerCount: 100 },
      { flightId: 'f2', passengerCount: 20 },
      { flightId: 'f3', passengerCount: 30 },
      { flightId: 'f4', passengerCount: 0 },
      { flightId: 'unknown', passengerCount: 50 },
    ];
    const captured = capturedHeatmapRun({ ...saved, inputSnapshotJson: JSON.stringify(input) })!;
    expect(captured.gateDemand).toEqual([
      { gateId: 'gate-1', passengers: 120 },
      { gateId: 'gate-3', passengers: 30 },
    ]);
    expect([...preparePassengerReplay(captured).gateDemand]).toEqual([
      ['gate-1', 120],
      ['gate-3', 30],
    ]);
  });
  it('uses the captured input allocation, never the current airport staffing or aggregate final metrics', () => {
    const zoneId = datasetFixture().baseline.capacities[0].zoneId;
    const run = {
      id: 'stored-run',
      status: 'completed',
      resultsValid: true,
      inputSnapshotJson: JSON.stringify({
        layout: {
          version: { id: 'captured-layout' },
          layout: { checkpoints: [{ id: zoneId, code: 'CP-1', capacityPerMinute: 7 }] },
        },
        scenario: {
          scenario: { id: 'scenario' },
          inputs: { staffAllocations: [{ checkpointId: zoneId }, { checkpointId: zoneId }] },
        },
      }),
      snapshots: [
        {
          zoneId,
          payloadJson: JSON.stringify({
            elapsedMinutes: 60,
            queuedPassengers: 20,
            processedPassengers: 400,
          }),
        },
      ],
      metrics: [],
    } as unknown as RunDetails;
    const result = capturedHeatmapRun(run)!;
    expect(result.layoutId).toBe('captured-layout');
    expect(result.capacities[0].staffCount).toBe(2);
    expect(result.snapshots).toEqual([
      { zoneId, elapsedMinutes: 60, queuedPassengers: 20, processedPassengers: 400 },
    ]);
    expect(capturedHeatmapRun({ ...run, inputSnapshotJson: 'broken JSON' })).toBeNull();
    expect(capturedHeatmapRun({ ...run, resultsValid: false })!.snapshots).toHaveLength(0);
  });
  it('adds the check-in desks as a heatmap zone only when the run reports them', () => {
    const desks = '60000000-0000-0000-0000-000000000901';
    const run = {
      id: 'stored-run',
      status: 'completed',
      resultsValid: true,
      inputSnapshotJson: JSON.stringify({
        layout: {
          version: { id: 'captured-layout' },
          layout: {
            checkpoints: [{ id: 'cp', code: 'CP-1', capacityPerMinute: 7 }],
            checkInAreas: [{ id: desks, code: 'CHECK-IN', serviceSeconds: 120 }],
          },
        },
        scenario: {
          scenario: { id: 'scenario' },
          inputs: {
            staffAllocations: [],
            checkIn: { areaId: desks, staffCount: 8, holdBaggageShare: 0.4 },
          },
        },
      }),
      snapshots: [
        {
          zoneId: desks,
          payloadJson: JSON.stringify({
            zoneId: desks,
            zoneType: 'checkIn',
            minute: 15,
            queuedPassengers: 30,
            processedPassengers: 40,
          }),
        },
      ],
      metrics: [],
    } as unknown as RunDetails;
    const result = capturedHeatmapRun(run)!;
    expect(result.capacities.at(-1)).toEqual({
      zoneId: desks,
      label: 'CHECK-IN',
      serviceRatePerMinute: 0.5,
      staffCount: 8,
      zoneType: 'checkIn',
    });
    expect(result.snapshots).toEqual([
      { zoneId: desks, elapsedMinutes: 15, queuedPassengers: 30, processedPassengers: 40 },
    ]);
    // A run without desk results (older captures, live progress) shows no empty check-in zone.
    expect(
      capturedHeatmapRun({ ...run, snapshots: [] })!.capacities.map((row) => row.zoneId),
    ).toEqual(['cp']);
  });
  it('ignores another run, deduplicates live frames and preserves observed history when final results arrive', () => {
    const current = datasetFixture().baseline,
      zoneId = current.capacities[0].zoneId;
    const progress = {
      runId: current.runId,
      status: 'running',
      revision: 1,
      resultsValid: false,
      simulationMinute: 3,
      snapshot: {
        totalZones: 1,
        zones: [{ zoneId, queuedPassengers: 25, processedPassengers: 10 }],
      },
    };
    expect(appendProgress(current, { ...progress, runId: 'other-run' })).toBe(current);
    const failed = { ...current, status: 'failed', snapshots: [] };
    expect(appendProgress(failed, progress)).toBe(failed);
    const merged = appendProgress(current, progress);
    expect(merged.snapshots).toHaveLength(3);
    expect(appendProgress(merged, progress)).toBe(merged);
    expect(
      mergeHeatmapRuns(merged, { ...current, snapshots: [merged.snapshots[2]] }).snapshots,
    ).toHaveLength(3);
    expect(
      appendProgress(current, {
        ...progress,
        snapshot: {
          totalZones: 1,
          zones: [{ zoneId, queuedPassengers: null, processedPassengers: null }],
        },
      }),
    ).toBe(current);
  });
});
