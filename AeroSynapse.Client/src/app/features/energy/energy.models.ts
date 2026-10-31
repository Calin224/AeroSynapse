export type CoefficientSource = 'configured-assumption' | 'default-assumption';
export type EnergyKind = 'checkpoint' | 'lighting' | 'resource';
export interface EnergyCheckpoint {
  id: string;
  label: string;
  minUnits: number;
  maxUnits: number;
  baselineUnits: number;
  candidateUnits: number;
  serviceRatePerMinute: number;
}
export interface EnergyLoad {
  id: string;
  kind: 'lighting' | 'resource';
  units: number;
  powerKw: number | null;
  baselineActiveMinutes: number;
  candidateActiveMinutes: number;
}
export interface EnergyRequest {
  contractVersion: '1.0';
  synthetic: true;
  seed: number;
  durationMinutes: number;
  passengerCount: number;
  arrivalRatePerMinute: number;
  checkpointPowerKw: number | null;
  tariffRonPerKwh: number | null;
  energyBudgetKwh: number | null;
  sensitivityPercent: number;
  checkpoints: EnergyCheckpoint[];
  loads: EnergyLoad[];
}
export interface EnergyItem {
  id: string;
  kind: EnergyKind;
  units: number;
  powerKw: number;
  activeMinutes: number;
  kwh: number;
  source: CoefficientSource;
}
export interface EnergyTraffic {
  populationCount: number;
  arrivedCount: number;
  notArrivedCount: number;
  processedCount: number;
  unprocessedCount: number;
  queuedCount: number;
  processingCount: number;
  throughputPerMinute: number;
  meanWaitMinutes: number | null;
  p95WaitMinutes: number | null;
}
export interface EnergyScenario {
  variant: 'baseline' | 'candidate' | 'optimized';
  assignment: { checkpointId: string; activeUnits: number }[];
  energy: {
    totalKwh: number;
    costRon: number | null;
    withinBudget: boolean | null;
    items: EnergyItem[];
  };
  traffic: EnergyTraffic;
  populationFingerprint: string;
  simulationRunId: string;
  simulationEngine: string;
  simulationLibraryVersion: string;
  snapshots: {
    minute: number;
    queuedCount: number;
    processedCount: number;
    energyKwhCumulative: number;
  }[];
}
export interface EnergyComparison {
  contractVersion: '1.0';
  engine: 'energy-analytics-1.0';
  synthetic: true;
  request: EnergyRequest;
  effectiveCoefficients: {
    id: string;
    kind: EnergyKind;
    powerKw: number;
    source: CoefficientSource;
  }[];
  baseline: EnergyScenario;
  candidate: EnergyScenario;
  sensitivity: {
    coefficientId: string;
    parameter: 'powerKw' | 'tariffRonPerKwh';
    lowerValue: number;
    baseValue: number;
    upperValue: number;
    lowerTotalKwh: number;
    baseTotalKwh: number;
    upperTotalKwh: number;
    lowerCostRon: number | null;
    baseCostRon: number | null;
    upperCostRon: number | null;
  }[];
  assumptions: string[];
  formulas: { energy: string; cost: string; sensitivity: string };
}
export interface EnergyOptimization {
  contractVersion: '1.0';
  synthetic: true;
  status: 'optimal' | 'infeasible';
  method: 'bounded-enumeration-simpy-1.0';
  request: EnergyRequest;
  configurationCount: number;
  feasibleCount: number;
  evaluatedCount: number;
  best: EnergyScenario | null;
  candidates: EnergyScenario[];
  diagnostics: string[];
  effectiveCoefficients: EnergyComparison['effectiveCoefficients'];
  assumptions: string[];
  formulas: EnergyComparison['formulas'];
}

export function energyDemoRequest(): EnergyRequest {
  return {
    contractVersion: '1.0',
    synthetic: true,
    seed: 22,
    durationMinutes: 60,
    passengerCount: 250,
    arrivalRatePerMinute: 6,
    checkpointPowerKw: null,
    tariffRonPerKwh: 1,
    energyBudgetKwh: 4,
    sensitivityPercent: 20,
    checkpoints: [
      {
        id: '05400000-0000-4000-8000-000000000001',
        label: 'Checkpoint sintetic A',
        minUnits: 0,
        maxUnits: 3,
        baselineUnits: 1,
        candidateUnits: 2,
        serviceRatePerMinute: 3,
      },
    ],
    loads: [
      {
        id: 'lumini-demo',
        kind: 'lighting',
        units: 20,
        powerKw: null,
        baselineActiveMinutes: 60,
        candidateActiveMinutes: 45,
      },
      {
        id: 'resursa-demo',
        kind: 'resource',
        units: 1,
        powerKw: null,
        baselineActiveMinutes: 60,
        candidateActiveMinutes: 30,
      },
    ],
  };
}

export function energyGridCount(request: EnergyRequest): number {
  return request.checkpoints.reduce((count, row) => count * (row.maxUnits - row.minUnits + 1), 1);
}

export function validateEnergy(request: EnergyRequest): string[] {
  const errors: string[] = [];
  const number = (value: unknown, min: number, max: number, integer = false): boolean =>
    typeof value === 'number' &&
    Number.isFinite(value) &&
    value >= min &&
    value <= max &&
    (!integer || Number.isInteger(value));
  const optional = (value: unknown, max: number): boolean =>
    value === null || number(value, 0, max);
  if (request.contractVersion !== '1.0' || request.synthetic !== true)
    errors.push('Contractul trebuie să confirme date exclusiv sintetice.');
  if (!number(request.seed, 0, 2147483647, true))
    errors.push('Seed: întreg între 0 și 2147483647.');
  if (!number(request.durationMinutes, 1, 180, true)) errors.push('Orizont: 1–180 minute întregi.');
  if (!number(request.passengerCount, 0, 2000, true))
    errors.push('Populație: 0–2000 pasageri întregi.');
  if (
    !number(request.arrivalRatePerMinute, 0, 1000) ||
    (request.arrivalRatePerMinute > 0 && request.arrivalRatePerMinute < 0.000001) ||
    (request.passengerCount > 0 && request.arrivalRatePerMinute === 0)
  )
    errors.push(
      'Rata de sosire: 0.000001–1000/min; zero este permis exclusiv pentru o populație nulă.',
    );
  if (!optional(request.checkpointPowerKw, 100))
    errors.push('Puterea checkpoint: gol sau 0–100 kW/unitate.');
  if (!optional(request.tariffRonPerKwh, 10000)) errors.push('Tarif: gol sau 0–10000 RON/kWh.');
  if (!optional(request.energyBudgetKwh, 1000000)) errors.push('Buget: gol sau 0–1000000 kWh.');
  if (!number(request.sensitivityPercent, 1, 50)) errors.push('Sensibilitate: 1–50%.');
  if (request.checkpoints.length < 1 || request.checkpoints.length > 6)
    errors.push('Configurează 1–6 checkpoint-uri.');
  const ids = new Set<string>(['checkpointPowerKw', 'tariffRonPerKwh']);
  for (const row of request.checkpoints) {
    if (
      !/^[\da-f]{8}-[\da-f]{4}-[\da-f]{4}-[\da-f]{4}-[\da-f]{12}$/.test(row.id) ||
      row.id === '00000000-0000-0000-0000-000000000000' ||
      ids.has(row.id)
    )
      errors.push('Checkpoint-urile trebuie să aibă identificatori UUID unici.');
    ids.add(row.id);
    if (!row.label.trim() || row.label.length > 80) errors.push('Nume checkpoint: 1–80 caractere.');
    if (
      ![row.minUnits, row.maxUnits, row.baselineUnits, row.candidateUnits].every((value) =>
        number(value, 0, 8, true),
      ) ||
      row.minUnits > row.maxUnits ||
      row.baselineUnits < row.minUnits ||
      row.baselineUnits > row.maxUnits ||
      row.candidateUnits < row.minUnits ||
      row.candidateUnits > row.maxUnits
    )
      errors.push(
        `${row.label || 'Checkpoint'}: unități întregi 0–8; referința și varianta trebuie să respecte minimul și maximul.`,
      );
    if (!number(row.serviceRatePerMinute, 0.000001, 1000))
      errors.push(
        `${row.label}: rata de serviciu trebuie să fie între 0.000001 și 1000 pasageri/min/unitate.`,
      );
  }
  if (request.loads.length > 16) errors.push('Maximum 16 consumatori auxiliari.');
  for (const row of request.loads) {
    if (!/^[A-Za-z0-9][A-Za-z0-9_-]{0,39}$/.test(row.id) || ids.has(row.id))
      errors.push(
        'Consumatorii trebuie să aibă identificatori unici de 1–40 caractere: litere, cifre, cratimă sau underscore; primul caracter alfanumeric.',
      );
    ids.add(row.id);
    if (!['lighting', 'resource'].includes(row.kind)) errors.push('Tip de consumator necunoscut.');
    if (!number(row.units, 0, 1000, true) || !optional(row.powerKw, 100))
      errors.push(`${row.id}: 0–1000 unități întregi și putere goală sau 0–100 kW/unitate.`);
    if (
      ![row.baselineActiveMinutes, row.candidateActiveMinutes].every((value) =>
        number(value, 0, request.durationMinutes),
      )
    )
      errors.push(`${row.id}: minutele active trebuie să fie între 0 și orizont.`);
  }
  return [...new Set(errors)];
}
