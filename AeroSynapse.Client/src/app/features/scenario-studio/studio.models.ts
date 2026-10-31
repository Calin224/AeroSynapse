import { ScenarioDetails, ScenarioInputs, SceneSummary } from '../operations/operations.models';
import { addMinutes } from '../../shared/absolute-time';

export interface StudioLayout {
  version: { id: string; version: number; sceneId: string; createdAtUtc: string };
  layout: {
    scene: SceneSummary & { isSynthetic: boolean };
    gates: { id: string; code: string; name: string; capacity: number; terminalId: string }[];
    checkpoints: {
      id: string;
      code: string;
      name: string;
      capacityPerMinute: number;
      terminalId: string;
    }[];
    staff: {
      id: string;
      code: string;
      displayName: string;
      terminalId: string;
      checkpointId: string | null;
    }[];
    routes: {
      id: string;
      code: string;
      fromCheckpointId: string;
      toGateId: string;
      terminalId: string;
    }[];
    /** Absent in layout versions published before check-in areas existed. */
    checkInAreas?: StudioCheckInArea[] | null;
  };
}
export interface StudioCheckInArea {
  id: string;
  code: string;
  name: string;
  deskCount: number;
  serviceSeconds: number;
  walkingSeconds: number;
  terminalId: string;
}
/** Two of three desks staffed and 40% hold baggage: a visible morning queue that drains as arrivals ease. */
export function defaultCheckIn(area: StudioCheckInArea): NonNullable<ScenarioInputs['checkIn']> {
  return {
    areaId: area.id,
    staffCount: Math.ceil((area.deskCount * 2) / 3),
    holdBaggageShare: 0.4,
  };
}
/**
 * Every placed security slot and every open check-in desk gets a named operator, so older scenarios show a full team.
 */
export function withOperators(draft: StudioDraft, layout: StudioLayout): StudioDraft {
  const inputs = draft.inputs;
  // Gate operators are no longer a role; saving drops them so the scenario validates again.
  const team = (inputs.operators ?? []).filter(
    (person) => person.role === 'security' || person.role === 'checkIn',
  );
  const named = new Set(team.map((person) => person.id));
  for (const row of inputs.staffAllocations)
    if (!named.has(row.staffMemberId))
      team.push({
        id: row.staffMemberId,
        name:
          layout.layout.staff.find((member) => member.id === row.staffMemberId)?.displayName ??
          'Operator securitate',
        role: 'security',
      });
  const desks = team.filter((person) => person.role === 'checkIn').length;
  for (let desk = desks; desk < (inputs.checkIn?.staffCount ?? 0); desk++)
    team.push({ id: crypto.randomUUID(), name: `Operator check-in ${desk + 1}`, role: 'checkIn' });
  inputs.operators = team;
  return draft;
}
export interface StudioDraft {
  code: string;
  name: string;
  description: string;
  sceneId: string;
  layoutVersionId: string;
  inputs: ScenarioInputs;
  revision?: number;
}
export type FieldErrors = Readonly<Record<string, readonly string[]>>;
export type InputEvent = ScenarioInputs['events'][number];
export const MINIMUM_DEPARTURE_SPACING_MINUTES = 60;
export const PRESETS = [
  {
    id: 'principal',
    name: 'Aurora · orar principal',
    detail: '6 ore · 5 zboruri · 600 pasageri',
    count: 120,
  },
  { id: 'calm', name: 'Trafic redus', detail: '6 ore · 5 zboruri · 250 pasageri', count: 50 },
  {
    id: 'bottleneck',
    name: 'Bottleneck · DEMO',
    detail: '6 ore · 600 pasageri · un control aglomerat',
    count: 120,
  },
  {
    id: 'peak',
    name: 'Vârf de trafic',
    detail: '6 ore · 5 zboruri · 900 pasageri',
    count: 180,
  },
  {
    id: 'workday',
    name: 'Zi de lucru · 8 ore',
    detail: '8 ore · 8 zboruri · 960 pasageri',
    count: 120,
    durationMinutes: 480,
    flightCount: 8,
  },
] as const;

export function fromScenario(value: ScenarioDetails): StudioDraft {
  const { code, name, description, sceneId, layoutVersionId, revision } = value.scenario;
  return {
    code,
    name,
    description,
    sceneId,
    layoutVersionId,
    revision,
    inputs: structuredClone(value.inputs),
  };
}
/** Passengers per minute one operator screens in the Bottleneck guide, whatever the layout says. */
export const BOTTLENECK_RATE = 3;
/** Screening lanes the Bottleneck checkpoint can physically open, one operator each. */
export const BOTTLENECK_LANES = 3;
/** Absolute arrival window anchored to the flight's departure. */
export function bottleneckArrivalWindow(departureAtUtc: string, startsAtUtc: string) {
  return {
    start: new Date(
      Math.max(Date.parse(startsAtUtc), Date.parse(addMinutes(departureAtUtc, -45))),
    ).toISOString(),
    end: addMinutes(departureAtUtc, -10),
  };
}
export function makeDraft(
  layout: StudioLayout,
  presetId: string | null,
  now = new Date(),
): StudioDraft {
  const preset = PRESETS.find((item) => item.id === presetId);
  const start = new Date(now);
  start.setUTCSeconds(0, 0);
  const inputs: ScenarioInputs = {
    startsAtUtc: start.toISOString(),
    durationMinutes: preset && 'durationMinutes' in preset ? preset.durationMinutes : 360,
    seed: 42,
    timeZone: 'Europe/Bucharest',
    snapshotIntervalMinutes: 1,
    minimumDepartureSpacingMinutes: MINIMUM_DEPARTURE_SPACING_MINUTES,
    flights: [],
    populations: [],
    flows: [],
    staffAllocations: [],
    events: [],
  };
  const draft: StudioDraft = {
    code: `DEMO-${crypto.randomUUID().slice(0, 8)}`,
    name: preset?.name ?? 'Scenariu demo nou',
    description:
      'Date sintetice pentru explorarea operațiunilor; nu reprezintă orarul real al unui aeroport.',
    sceneId: layout.layout.scene.id,
    layoutVersionId: layout.version.id,
    inputs,
  };
  if (!preset) return draft;
  const routes = layout.layout.routes.filter(
    (route) =>
      layout.layout.gates.some((gate) => gate.id === route.toGateId) &&
      layout.layout.checkpoints.some((checkpoint) => checkpoint.id === route.fromCheckpointId),
  );
  if (!routes.length)
    throw new Error(
      'Presetul necesită cel puțin un traseu între un checkpoint și o poartă în layout.',
    );
  const gates = [...new Set(routes.map((route) => route.toGateId))];
  const checkpointCode = (id: string) =>
    layout.layout.checkpoints.find((checkpoint) => checkpoint.id === id)?.code ?? id;
  const flightCount = 'flightCount' in preset ? preset.flightCount : 5;
  let departureAtUtc = inputs.startsAtUtc;
  for (let index = 0; index < flightCount; index++) {
    departureAtUtc = addMinutes(departureAtUtc, MINIMUM_DEPARTURE_SPACING_MINUTES);
    const gateId = gates[index % gates.length];
    // Flights take the checkpoints toward their gate in turn, so no single control carries the whole day.
    const toGate = routes
      .filter((item) => item.toGateId === gateId)
      .sort((a, b) =>
        checkpointCode(a.fromCheckpointId).localeCompare(checkpointCode(b.fromCheckpointId)),
      );
    const route = toGate[index % toGate.length];
    const flightId = crypto.randomUUID(),
      populationId = crypto.randomUUID();
    inputs.flights.push({
      id: flightId,
      code: `DEMO ${101 + index}`,
      gateId,
      passengerCapacity: preset.count,
      departureAtUtc,
      boardingOpensAtUtc: addMinutes(departureAtUtc, -12),
      boardingClosesAtUtc: addMinutes(departureAtUtc, -2),
      delayMinutes: 0,
      status: 'scheduled',
      isSynthetic: true,
    });
    inputs.populations.push({
      id: populationId,
      name: `Pasageri DEMO ${101 + index}`,
      count: preset.count,
    });
    inputs.flows.push({
      id: crypto.randomUUID(),
      flightId,
      populationId,
      routeId: route.id,
      passengerCount: preset.count,
      // A long day's passengers arrive in the 90 minutes before their flight, not all at the start.
      startsAtUtc: new Date(
        Math.max(+start, Date.parse(addMinutes(departureAtUtc, -90))),
      ).toISOString(),
      endsAtUtc: addMinutes(departureAtUtc, -2),
    });
  }
  inputs.staffAllocations = layout.layout.staff
    .filter(
      (member) =>
        member.checkpointId &&
        layout.layout.checkpoints.some(
          (checkpoint) =>
            checkpoint.id === member.checkpointId && checkpoint.terminalId === member.terminalId,
        ),
    )
    .map((member) => ({ staffMemberId: member.id, checkpointId: member.checkpointId! }));
  // The Bottleneck guide isolates one control, so its passengers go straight to security.
  const desks = layout.layout.checkInAreas?.[0];
  if (desks && preset.id !== 'bottleneck') inputs.checkIn = defaultCheckIn(desks);
  if (preset.id === 'bottleneck') {
    const checkpoint = layout.layout.checkpoints.find(
      (cp) =>
        layout.layout.staff.filter((member) => member.terminalId === cp.terminalId).length >=
          BOTTLENECK_LANES &&
        inputs.flights.every((flight) =>
          routes.some(
            (route) => route.fromCheckpointId === cp.id && route.toGateId === flight.gateId,
          ),
        ),
    );
    if (!checkpoint || !layout.layout.scene.isSynthetic)
      throw new Error(
        'Bottleneck necesită un layout sintetic, un control cu trei persoane disponibile și trasee spre toate porțile demo.',
      );
    const staff = layout.layout.staff.filter(
      (member) => member.terminalId === checkpoint.terminalId,
    );
    inputs.staffAllocations = [{ staffMemberId: staff[0].id, checkpointId: checkpoint.id }];
    for (const flow of inputs.flows) {
      const flight = inputs.flights.find((row) => row.id === flow.flightId)!;
      flow.routeId = routes.find(
        (route) => route.fromCheckpointId === checkpoint.id && route.toGateId === flight.gateId,
      )!.id;
    }
    const bottleneckArrival = (departureAtUtc: string) => {
      const window = bottleneckArrivalWindow(departureAtUtc, start.toISOString());
      // Adapt absolute scheduling to the legacy generator's distribution contract only here.
      const minimum = (Date.parse(window.start) - +start) / 60000;
      const maximum = (Date.parse(window.end) - +start) / 60000;
      const mean = (minimum + maximum) / 2;
      return { kind: 'uniform', minimum, maximum, mean, stdDev: 0, mode: mean };
    };
    const constant = (value: number) => ({
      kind: 'constant',
      minimum: value,
      maximum: value,
      mean: value,
      stdDev: 0,
      mode: value,
    });
    inputs.passengerGeneration = {
      generatorVersion: '1.0',
      batches: inputs.flights.map((flight) => ({
        flightId: flight.id,
        checkpointId: checkpoint.id,
        countMode: 'full',
        modeledCount: flight.passengerCapacity,
        arrival: bottleneckArrival(flight.departureAtUtc),
        // The generator divides this multiplier by the checkpoint's configured rate.
        service: constant(checkpoint.capacityPerMinute / BOTTLENECK_RATE),
        travel: constant(1),
        groups: [
          {
            kind: 'solo',
            weight: 1,
            minSize: 1,
            maxSize: 1,
            serviceMultiplier: 1,
            travelMultiplier: 1,
          },
        ],
        surges: [],
      })),
    };
    draft.description =
      'DEMO sintetic Bottleneck: 600 de pasageri sosesc între 45 și 10 minute înainte de zborul lor; o linie de control procesează 3 pasageri/min, cu un singur operator. Comparația caută până la trei operatori fără a modifica populația sau seed-ul.';
  }
  return withOperators(draft, layout);
}

export function basicErrors(draft: StudioDraft): FieldErrors {
  const errors: Record<string, string[]> = {};
  const integer = (
    value: number | null | undefined,
    field: string,
    min: number,
    max = 2147483647,
  ) => {
    if (!Number.isInteger(value) || value! < min || value! > max)
      errors[field] = [`Valoare obligatorie: număr întreg între ${min} și ${max}.`];
  };
  const timestamp = (value: string | undefined | null, field: string) => {
    if (!value?.endsWith('Z') || !Number.isFinite(Date.parse(value)))
      errors[field] = ['Este necesară o dată și oră UTC validă.'];
  };
  if (!draft.code.trim() || draft.code.length > 64)
    errors['code'] = ['Codul trebuie să aibă 1–64 de caractere.'];
  if (!draft.name.trim() || draft.name.length > 160)
    errors['name'] = ['Numele trebuie să aibă 1–160 de caractere.'];
  if (draft.description.length > 2000)
    errors['description'] = ['Descrierea poate avea maximum 2000 de caractere.'];
  const input = draft.inputs;
  integer(input.durationMinutes, 'inputs.durationMinutes', 1, 10080);
  integer(input.seed, 'inputs.seed', 0);
  if (input.snapshotIntervalMinutes != null)
    integer(input.snapshotIntervalMinutes, 'inputs.snapshotIntervalMinutes', 1, 5);
  if (input.minimumDepartureSpacingMinutes != null)
    integer(
      input.minimumDepartureSpacingMinutes,
      'inputs.minimumDepartureSpacingMinutes',
      1,
      10080,
    );
  timestamp(input.startsAtUtc, 'inputs.startsAtUtc');
  try {
    if (input.timeZone !== 'UTC' && !input.timeZone.includes('/')) throw new Error();
    new Intl.DateTimeFormat('ro', { timeZone: input.timeZone });
  } catch {
    errors['inputs.timeZone'] = [
      'Valoare acceptată: UTC sau un fus IANA valid, de exemplu Europe/Bucharest.',
    ];
  }
  input.flights.forEach((flight, index) => {
    const path = `inputs.flights[${index}]`;
    integer(flight.passengerCapacity, path + '.passengerCapacity', 1);
    integer(flight.delayMinutes ?? 0, path + '.delayMinutes', 0, 10080);
    timestamp(flight.departureAtUtc, path + '.departureAtUtc');
    if (flight.boardingOpensAtUtc != null)
      timestamp(flight.boardingOpensAtUtc, path + '.boardingOpensAtUtc');
    if (flight.boardingClosesAtUtc != null)
      timestamp(flight.boardingClosesAtUtc, path + '.boardingClosesAtUtc');
  });
  Object.assign(errors, departureSpacingErrors(input));
  input.populations.forEach((row, index) =>
    integer(row.count, `inputs.populations[${index}].count`, 1),
  );
  input.flows.forEach((row, index) => {
    integer(row.passengerCount, `inputs.flows[${index}].passengerCount`, 1);
    timestamp(row.startsAtUtc, `inputs.flows[${index}].startsAtUtc`);
    timestamp(row.endsAtUtc, `inputs.flows[${index}].endsAtUtc`);
  });
  input.events.forEach((row, index) =>
    timestamp(row.occurredAtUtc, `inputs.events[${index}].occurredAtUtc`),
  );
  return errors;
}

export function departureSpacingErrors(input: ScenarioInputs): FieldErrors {
  const minimum = input.minimumDepartureSpacingMinutes;
  if (!Number.isInteger(minimum) || minimum! < 1 || minimum! > 10080) return {};
  const departures = input.flights
    .map((flight, index) => ({
      flight,
      index,
      time: Date.parse(flight.departureAtUtc) + (flight.delayMinutes ?? 0) * 60000,
    }))
    .filter(
      ({ flight, time }) =>
        flight.status !== 'cancelled' &&
        Number.isFinite(time) &&
        Number.isInteger(flight.delayMinutes ?? 0) &&
        (flight.delayMinutes ?? 0) >= 0 &&
        (flight.delayMinutes ?? 0) <= 10080,
    )
    .sort((a, b) => a.time - b.time);
  const errors: Record<string, string[]> = {};
  for (let index = 1; index < departures.length; index++) {
    const first = departures[index - 1],
      second = departures[index];
    if (second.time - first.time >= minimum! * 60000) continue;
    const message = `Plecările ${first.flight.code} și ${second.flight.code} trebuie distanțate cu minimum ${minimum} de minute, inclusiv întârzierile configurate.`;
    for (const departure of [first, second]) {
      const field = `inputs.flights[${departure.index}].departureAtUtc`;
      (errors[field] ??= []).push(message);
    }
  }
  return errors;
}

export function shiftStart(draft: StudioDraft, start: string): void {
  const offset = Date.parse(start) - Date.parse(draft.inputs.startsAtUtc);
  if (!Number.isFinite(offset)) {
    draft.inputs.startsAtUtc = start;
    return;
  }
  const shifted = (value: string) => {
    const time = Date.parse(value) + offset;
    return Number.isFinite(time) && Math.abs(time) <= 8640000000000000
      ? new Date(time).toISOString()
      : '';
  };
  for (const flight of draft.inputs.flights) {
    flight.departureAtUtc = shifted(flight.departureAtUtc);
    if (flight.boardingOpensAtUtc) flight.boardingOpensAtUtc = shifted(flight.boardingOpensAtUtc);
    if (flight.boardingClosesAtUtc)
      flight.boardingClosesAtUtc = shifted(flight.boardingClosesAtUtc);
  }
  for (const flow of draft.inputs.flows) {
    flow.startsAtUtc = shifted(flow.startsAtUtc);
    flow.endsAtUtc = shifted(flow.endsAtUtc);
  }
  for (const event of draft.inputs.events) event.occurredAtUtc = shifted(event.occurredAtUtc);
  draft.inputs.startsAtUtc = start;
}
