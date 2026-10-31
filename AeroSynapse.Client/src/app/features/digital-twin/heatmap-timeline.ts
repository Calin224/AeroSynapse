import {
  buildHeatmapFrame,
  CONGESTION_BANDS,
  CongestionBand,
  HeatmapRun,
  ZoneSnapshot,
} from './heatmap-model';

export interface CongestionMoment {
  minute: number;
  band: CongestionBand;
}

// Known congestion takes precedence; incomplete data must not look fully clear.
const SEVERITY: CongestionBand[] = ['critical', 'warning', 'unavailable', 'low', 'closed'];

/** One worst-zone state per snapshot, using exactly the map's capacity and validation rules. */
export function congestionTimeline(
  run: HeatmapRun,
  minutes: readonly number[],
): CongestionMoment[] {
  const snapshots = new Map<number, ZoneSnapshot[]>();
  for (const row of run.snapshots) {
    const rows = snapshots.get(row.elapsedMinutes) ?? [];
    rows.push(row);
    snapshots.set(row.elapsedMinutes, rows);
  }
  return [...new Set(minutes)]
    .filter((minute) => Number.isFinite(minute) && minute >= 0)
    .sort((a, b) => a - b)
    .map((minute) => {
      const frame = buildHeatmapFrame(
        { baseline: { ...run, snapshots: snapshots.get(minute) ?? [] } },
        'baseline',
        minute,
      );
      const bands = new Set(frame.zones.map((zone) => zone.band));
      if (frame.missingZones.length) bands.add('unavailable');
      return { minute, band: SEVERITY.find((band) => bands.has(band)) ?? 'unavailable' };
    });
}

/** Playback displays the latest snapshot at or before the cursor; do not interpolate severity. */
export function congestionAt(
  timeline: readonly CongestionMoment[],
  minute: number,
): CongestionBand {
  let low = 0;
  let high = timeline.length;
  while (low < high) {
    const middle = (low + high) >>> 1;
    if (timeline[middle].minute <= minute) low = middle + 1;
    else high = middle;
  }
  return timeline[low - 1]?.band ?? 'unavailable';
}

/** Hard colour stops preserve the actual duration of each observed state. */
export function congestionGradient(
  timeline: readonly CongestionMoment[],
  start: number,
  end: number,
): string {
  if (end <= start || !timeline.length)
    return CONGESTION_BANDS[congestionAt(timeline, start)].color;
  const spans: { start: number; end: number; band: CongestionBand }[] = [];
  let cursor = start;
  let band = congestionAt(timeline, start);
  for (const moment of timeline) {
    if (moment.minute <= start || moment.minute >= end || moment.band === band) continue;
    spans.push({ start: cursor, end: moment.minute, band });
    cursor = moment.minute;
    band = moment.band;
  }
  spans.push({ start: cursor, end, band });
  const percent = (minute: number) => Number((((minute - start) / (end - start)) * 100).toFixed(6));
  return `linear-gradient(to right, ${spans
    .map((span) => {
      const color = CONGESTION_BANDS[span.band].color;
      return `${color} ${percent(span.start)}%, ${color} ${percent(span.end)}%`;
    })
    .join(', ')})`;
}

export const CONGESTION_SLIDER_STYLES = `
  input.congestion-slider {
    appearance: none;
    height: 22px;
    padding: 0;
    margin: 0;
    border: 0;
    background: transparent;
    cursor: pointer;
  }
  input.congestion-slider::-webkit-slider-runnable-track {
    height: 8px;
    border-radius: 4px;
    background: var(--congestion-track);
  }
  input.congestion-slider::-moz-range-track {
    height: 8px;
    border-radius: 4px;
    background: var(--congestion-track);
  }
  input.congestion-slider::-moz-range-progress {
    background: transparent;
  }
  input.congestion-slider::-webkit-slider-thumb {
    appearance: none;
    width: 18px;
    height: 18px;
    margin-top: -5px;
    border: 2px solid var(--panel, white);
    border-radius: 50%;
    background: var(--congestion-thumb);
    box-shadow: 0 0 0 1px var(--text);
  }
  input.congestion-slider::-moz-range-thumb {
    width: 14px;
    height: 14px;
    border: 2px solid var(--panel, white);
    border-radius: 50%;
    background: var(--congestion-thumb);
    box-shadow: 0 0 0 1px var(--text);
  }
`;
