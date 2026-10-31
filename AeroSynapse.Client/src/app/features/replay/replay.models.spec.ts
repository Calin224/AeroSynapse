import { prepareReplay, replayEvents, replayFrame, validBookmark } from './replay.models';
import { ids, replayData, replayEnvelope } from './replay.testing';

describe('Immutable replay document and time selection', () => {
  beforeEach(async () =>
    vi.stubGlobal(
      'crypto',
      (await vi.importActual<{ webcrypto: Crypto }>('node:crypto')).webcrypto,
    ),
  );
  afterEach(() => vi.unstubAllGlobals());
  it('returns identical observed KPI and aggregate data after seeking away and back to T', async () => {
    const envelope = await replayEnvelope(),
      prepared = await prepareReplay(envelope, ids.run),
      original = JSON.stringify(prepared.data);
    const first = replayFrame(prepared, 2.75);
    replayFrame(prepared, 6);
    replayFrame(prepared, 0);
    expect(replayFrame(prepared, 2.75)).toEqual(first);
    expect(first).toMatchObject({ time: 2.75, anchor: 2, complete: true });
    expect(first.zones[0].sample?.kpi?.meanWaitMinutes).toBe(1.25);
    expect(JSON.stringify(prepared.data)).toBe(original);
    expect(Object.isFrozen(first.zones[0].sample?.kpi)).toBe(true);
    expect(replayFrame(prepared, -5).time).toBe(0);
    expect(replayFrame(prepared, 100).time).toBe(6);
  });
  it('replays the check-in desks as their own zone without changing control completeness', async () => {
    const desks = '50000000-0000-0000-0000-000000000901';
    const withDesks = () => {
      const data = replayData();
      data.captured.layout.layout.checkInAreas = [
        {
          id: desks,
          code: 'CHECK-IN',
          name: 'Check-in',
          deskCount: 12,
          serviceSeconds: 120,
          walkingSeconds: 90,
          terminalId: ids.scene,
        },
      ];
      data.captured.scenario.inputs.checkIn = {
        areaId: desks,
        staffCount: 8,
        holdBaggageShare: 0.4,
      };
      for (const row of data.samples.filter((sample) => sample.zoneId === ids.cp))
        data.samples.push({
          zoneId: desks,
          minute: row.minute,
          recordedAtUtc: row.recordedAtUtc,
          queuedPassengers: 12,
          processedPassengers: 30,
          kpi: null,
          checkIn: {
            zoneId: desks,
            zoneType: 'checkIn',
            minute: row.minute,
            queuedPassengers: 12,
            processedPassengers: 30,
            maximumQueue: 15,
            meanWaitMinutes: 3.5,
            p95WaitMinutes: 7,
            capacity: 8,
            utilization: 0.9,
          },
        });
      return data;
    };
    const prepared = await prepareReplay(await replayEnvelope(withDesks()), ids.run);
    const frame = replayFrame(prepared, 2.75);
    expect(frame.complete).toBe(true);
    expect(frame.zones).toHaveLength(2);
    expect(frame.desks?.sample?.checkIn?.meanWaitMinutes).toBe(3.5);
    expect(prepared.heatmap.baseline.capacities.at(-1)).toMatchObject({
      zoneId: desks,
      zoneType: 'checkIn',
      serviceRatePerMinute: 0.5,
      staffCount: 8,
    });
    // A desk sample posing as a control KPI, or a control carrying desk data, is not this run's capture.
    const forged = withDesks();
    forged.samples.find((row) => row.zoneId === desks)!.kpi = forged.samples[0].kpi;
    await expect(prepareReplay(await replayEnvelope(forged), ids.run)).rejects.toThrow();
    const mixed = withDesks();
    mixed.samples[0].checkIn = mixed.samples.find((row) => row.zoneId === desks)!.checkIn;
    await expect(prepareReplay(await replayEnvelope(mixed), ids.run)).rejects.toThrow();
    // A bookmark may mark the simulated desks, but only in a run that simulated them.
    const mark = {
      id: ids.mark,
      runId: ids.run,
      minute: 2.5,
      label: 'Coadă la ghișee',
      note: '',
      zoneId: desks,
      revision: 0,
    };
    expect(validBookmark(mark, prepared.data)).toBe(true);
    const plain = await prepareReplay(await replayEnvelope(), ids.run);
    expect(validBookmark(mark, plain.data)).toBe(false);
    // Runs without simulated desks keep the previous frame.
    expect(replayFrame(plain, 2.75).desks).toBeNull();
  });
  it('keeps missing windows and missing zones explicit rather than carrying old or future values', async () => {
    const data = replayData();
    data.samples = data.samples.filter(
      (row) => row.minute !== 2 && !(row.minute === 4 && row.zoneId === ids.otherCp),
    );
    const prepared = await prepareReplay(await replayEnvelope(data));
    expect(replayFrame(prepared, 3).zones.every((row) => row.sample === null)).toBe(true);
    expect(replayFrame(prepared, 4.5).complete).toBe(false);
    expect(replayFrame(prepared, 4.5).zones[1].sample).toBeNull();
    expect(replayFrame(prepared, 0).zones[0].sample?.kpi?.meanWaitMinutes).toBeNull();
  });
  it('does not invent a cadence for sparse historical observations', async () => {
    const data = replayData();
    data.intervalMinutes = null;
    const prepared = await prepareReplay(await replayEnvelope(data));
    expect(replayFrame(prepared, 2).complete).toBe(true);
    expect(replayFrame(prepared, 2.1).complete).toBe(false);
  });
  it('preserves fractional event time and captured incident duration', async () => {
    const prepared = await prepareReplay(await replayEnvelope());
    expect(replayEvents(prepared)[0]).toMatchObject({
      id: ids.event,
      minute: 2.5,
      duration: 1,
      checkpointId: ids.cp,
    });
    expect(
      validBookmark(
        {
          id: ids.mark,
          runId: ids.run,
          minute: 2.5,
          label: 'Surge',
          note: '',
          zoneId: ids.cp,
          revision: 0,
        },
        prepared.data,
      ),
    ).toBe(true);
  });
  it('rejects a changed payload and a mismatched requested runId', async () => {
    const envelope = await replayEnvelope();
    await expect(
      prepareReplay({ ...envelope, payloadJson: envelope.payloadJson + ' ' }),
    ).rejects.toThrow('Amprenta');
    await expect(prepareReplay(envelope, ids.otherRun)).rejects.toThrow('invalid');
    await expect(prepareReplay({ ...envelope, contractVersion: '2.0' } as never)).rejects.toThrow(
      'invalid',
    );
  });
  it.each([
    'foreign-zone',
    'foreign-kpi-run',
    'duplicate',
    'counts',
    'time',
    'failed',
    'bookmark',
  ] as const)('rejects a self-consistent hash with invalid %s content', async (reason) => {
    const data = replayData(),
      envelope = await replayEnvelope();
    if (reason === 'foreign-zone') data.samples[0].zoneId = ids.mark;
    if (reason === 'foreign-kpi-run') data.samples[0].kpi!.runId = ids.otherRun;
    if (reason === 'duplicate') data.samples.push(data.samples[0]);
    if (reason === 'counts') data.samples[0].kpi!.queuedPassengers++;
    if (reason === 'time') data.samples[0].recordedAtUtc = '2026-10-16T12:00:00Z';
    if (reason === 'failed') data.run.status = 'failed';
    Object.assign(envelope, await replayEnvelope(data));
    if (reason === 'bookmark')
      envelope.bookmarks = [
        {
          id: ids.mark,
          runId: ids.otherRun,
          minute: 2,
          label: 'X',
          note: '',
          zoneId: null,
          revision: 0,
        },
      ];
    await expect(prepareReplay(envelope)).rejects.toThrow('invalid');
  });
});
