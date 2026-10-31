const formatters = new Map<string, Intl.DateTimeFormat>();
function formatter(timeZone: string) {
  let result = formatters.get(timeZone);
  if (!result) {
    result = new Intl.DateTimeFormat('en-CA', {
      timeZone,
      year: 'numeric',
      month: '2-digit',
      day: '2-digit',
      hour: '2-digit',
      minute: '2-digit',
      second: '2-digit',
      fractionalSecondDigits: 3,
      hourCycle: 'h23',
    });
    formatters.set(timeZone, result);
  }
  return result;
}
function wallTime(utc: string, timeZone: string) {
  const date = new Date(utc);
  if (!Number.isFinite(+date)) return '';
  try {
    const parts = Object.fromEntries(
      formatter(timeZone)
        .formatToParts(date)
        .map((part) => [part.type, part.value]),
    );
    return `${parts['year']}-${parts['month']}-${parts['day']}T${parts['hour']}:${parts['minute']}:${parts['second']}.${parts['fractionalSecond']}`;
  } catch {
    return '';
  }
}
/** An absolute calendar date and HH:MM in the scenario's zone, independent of the browser's zone. */
export function localDateTime(utc: string | null | undefined, timeZone: string): string {
  const value = utc ? wallTime(utc, timeZone) : '';
  return value.endsWith(':00.000') ? value.slice(0, 16) : value;
}
export function clockTime(utc: string | null | undefined, timeZone: string): string {
  return localDateTime(utc, timeZone).slice(11, 16) || '—';
}
export function absoluteTimeLabel(utc: string | null | undefined, timeZone: string): string {
  const value = localDateTime(utc, timeZone);
  return value ? `${value.slice(11, 16)} · ${value.slice(0, 10)}` : '—';
}
/** Resolve a wall clock to UTC. Reject impossible dates and ambiguous DST times unless preserving an existing instant. */
export function utcDateTime(
  value: string,
  timeZone: string,
  previous?: string | null,
): string | null {
  if (!/^\d{4}-\d\d-\d\dT\d\d:\d\d(?::\d\d(?:\.\d{1,3})?)?$/.test(value)) return null;
  const canonical =
    value.length === 16
      ? `${value}:00.000`
      : value.length === 19
        ? `${value}.000`
        : value.padEnd(23, '0');
  const wall = Date.parse(`${canonical}Z`);
  if (!Number.isFinite(wall) || new Date(wall).toISOString().slice(0, 23) !== canonical)
    return null;
  const candidates = new Set<string>();
  for (const hours of [-36, -12, 0, 12, 36]) {
    const sample = wall + hours * 3600000;
    const represented = wallTime(new Date(sample).toISOString(), timeZone);
    if (!represented) return null;
    const offset = Date.parse(`${represented}Z`) - sample;
    const candidate = new Date(wall - offset).toISOString();
    if (wallTime(candidate, timeZone) === canonical) candidates.add(candidate);
  }
  if (previous && Number.isFinite(Date.parse(previous))) {
    const existing = new Date(previous).toISOString();
    if (candidates.has(existing)) return existing;
  }
  return candidates.size === 1 ? [...candidates][0] : null;
}
/** Shift an absolute timestamp by a duration, never by a position on the simulation timeline. */
export function addMinutes(utc: string, duration: number): string {
  const timestamp = Date.parse(utc) + duration * 60000;
  return Number.isFinite(timestamp) && Math.abs(timestamp) <= 8640000000000000
    ? new Date(timestamp).toISOString()
    : '';
}
