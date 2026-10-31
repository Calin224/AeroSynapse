import {
  absoluteTimeLabel,
  addMinutes,
  clockTime,
  localDateTime,
  utcDateTime,
} from './absolute-time';

describe('Absolute scenario times', () => {
  it('displays the scenario timezone and date across midnight, regardless of the browser timezone', () => {
    expect(localDateTime('2026-10-31T22:30:00Z', 'Europe/Bucharest')).toBe('2026-11-01T00:30');
    expect(absoluteTimeLabel('2026-10-31T22:30:00Z', 'Europe/Bucharest')).toBe(
      '00:30 · 2026-11-01',
    );
    expect(clockTime('2026-10-31T22:30:00Z', 'UTC')).toBe('22:30');
    expect(utcDateTime('2026-11-01T00:30', 'Europe/Bucharest')).toBe('2026-10-31T22:30:00.000Z');
  });

  it('uses the timezone offset for the chosen date, including non-hour offsets', () => {
    expect(utcDateTime('2026-07-01T13:15', 'Europe/Bucharest')).toBe('2026-07-01T10:15:00.000Z');
    expect(utcDateTime('2026-11-01T13:15', 'Europe/Bucharest')).toBe('2026-11-01T11:15:00.000Z');
    expect(utcDateTime('2026-11-01T13:15', 'Asia/Kathmandu')).toBe('2026-11-01T07:30:00.000Z');
  });

  it('preserves seconds and milliseconds from existing scenario timestamps', () => {
    const utc = '2026-11-01T10:15:42.125Z';
    expect(localDateTime(utc, 'Europe/Bucharest')).toBe('2026-11-01T12:15:42.125');
    expect(utcDateTime('2026-11-01T12:15:42.125', 'Europe/Bucharest')).toBe(utc);
    expect(utcDateTime('2026-11-01T12:15:42.1', 'Europe/Bucharest')).toBe(
      '2026-11-01T10:15:42.100Z',
    );
  });

  it('rejects invalid calendar values, missing dates and unknown timezones', () => {
    for (const value of [
      '',
      '12:30',
      '60',
      '2026-02-30T12:30',
      '2026-11-01T24:00',
      '2026-11-01T12:60',
    ])
      expect(utcDateTime(value, 'UTC')).toBeNull();
    expect(utcDateTime('2026-11-01T12:30', 'Invalid/Zone')).toBeNull();
    expect(localDateTime('invalid', 'UTC')).toBe('');
    expect(absoluteTimeLabel('', 'UTC')).toBe('—');
  });

  it('rejects nonexistent spring times and requires an existing instant to retain an ambiguous autumn time', () => {
    expect(utcDateTime('2026-03-29T03:30', 'Europe/Bucharest')).toBeNull();
    expect(utcDateTime('2026-10-25T03:30', 'Europe/Bucharest')).toBeNull();
    expect(utcDateTime('2026-10-25T03:30', 'Europe/Bucharest', '2026-10-25T00:30:00Z')).toBe(
      '2026-10-25T00:30:00.000Z',
    );
    expect(utcDateTime('2026-10-25T03:30', 'Europe/Bucharest', '2026-10-25T01:30:00Z')).toBe(
      '2026-10-25T01:30:00.000Z',
    );
    expect(utcDateTime('2026-10-25T03:30', 'Europe/Bucharest', '2026-10-24T01:30:00Z')).toBeNull();
  });

  it('shifts an absolute departure by an actual duration over midnight and DST', () => {
    expect(addMinutes('2026-10-31T23:45:00Z', 60)).toBe('2026-11-01T00:45:00.000Z');
    expect(localDateTime(addMinutes('2026-03-29T00:30:00Z', 60), 'Europe/Bucharest')).toBe(
      '2026-03-29T04:30',
    );
    expect(addMinutes('invalid', 60)).toBe('');
    expect(addMinutes('2026-11-01T00:00:00Z', Infinity)).toBe('');
  });
});
