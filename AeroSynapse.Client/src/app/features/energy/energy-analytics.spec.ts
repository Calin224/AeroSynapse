import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { apiErrorInterceptor } from '../../core/http/api-error.interceptor';
import { EnergyAnalytics } from './energy-analytics';
import {
  EnergyComparison,
  EnergyOptimization,
  EnergyRequest,
  EnergyScenario,
  energyDemoRequest,
  energyGridCount,
  validateEnergy,
} from './energy.models';

function scenario(
  variant: EnergyScenario['variant'],
  request = energyDemoRequest(),
): EnergyScenario {
  const activeUnits =
    variant === 'baseline'
      ? request.checkpoints[0].baselineUnits
      : request.checkpoints[0].candidateUnits;
  return {
    variant,
    assignment: [{ checkpointId: request.checkpoints[0].id, activeUnits }],
    energy: {
      totalKwh: variant === 'baseline' ? 2.9 : 3.95,
      costRon: request.tariffRonPerKwh === null ? null : 3.95 * request.tariffRonPerKwh,
      withinBudget: request.energyBudgetKwh === null ? null : true,
      items: [
        {
          id: request.checkpoints[0].id,
          kind: 'checkpoint',
          units: activeUnits,
          powerKw: request.checkpointPowerKw ?? 1.5,
          activeMinutes: request.durationMinutes,
          kwh: activeUnits * 1.5,
          source:
            request.checkpointPowerKw === null ? 'default-assumption' : 'configured-assumption',
        },
      ],
    },
    traffic: {
      populationCount: request.passengerCount,
      arrivedCount: 250,
      notArrivedCount: 0,
      processedCount: variant === 'baseline' ? 170 : 245,
      unprocessedCount: variant === 'baseline' ? 80 : 5,
      queuedCount: 3,
      processingCount: 2,
      throughputPerMinute: 4.08,
      meanWaitMinutes: 0.7,
      p95WaitMinutes: 2.1,
    },
    populationFingerprint: 'synthetic-population-sha256',
    simulationRunId: `run-${variant}`,
    simulationEngine: 'simpy-security-1.0',
    simulationLibraryVersion: '4.1.1',
    snapshots: [{ minute: 60, queuedCount: 3, processedCount: 245, energyKwhCumulative: 3.95 }],
  };
}
function comparison(request = energyDemoRequest()): EnergyComparison {
  return {
    contractVersion: '1.0',
    engine: 'energy-analytics-1.0',
    synthetic: true,
    request: structuredClone(request),
    effectiveCoefficients: [
      { id: 'checkpointPowerKw', kind: 'checkpoint', powerKw: 1.5, source: 'default-assumption' },
    ],
    baseline: scenario('baseline', request),
    candidate: scenario('candidate', request),
    sensitivity: [
      {
        coefficientId: 'checkpointPowerKw',
        parameter: 'powerKw',
        lowerValue: 1.2,
        baseValue: 1.5,
        upperValue: 1.8,
        lowerTotalKwh: 3.35,
        baseTotalKwh: 3.95,
        upperTotalKwh: 4.55,
        lowerCostRon: request.tariffRonPerKwh === null ? null : 3.35,
        baseCostRon: request.tariffRonPerKwh === null ? null : 3.95,
        upperCostRon: request.tariffRonPerKwh === null ? null : 4.55,
      },
    ],
    assumptions: ['All power coefficients are synthetic hypotheses, never measured.'],
    formulas: {
      energy: 'E = units * kW * minutes / 60',
      cost: 'RON = kWh * RON/kWh',
      sensitivity: 'One coefficient at a time, traffic fixed.',
    },
  };
}
function optimization(request = energyDemoRequest()): EnergyOptimization {
  return {
    contractVersion: '1.0',
    synthetic: true,
    status: 'optimal',
    method: 'bounded-enumeration-simpy-1.0',
    request: structuredClone(request),
    configurationCount: 4,
    feasibleCount: 3,
    evaluatedCount: 4,
    best: scenario('optimized', request),
    candidates: [scenario('optimized', request)],
    diagnostics: ['Bounded synthetic grid only.'],
    effectiveCoefficients: comparison(request).effectiveCoefficients,
    assumptions: comparison(request).assumptions,
    formulas: comparison(request).formulas,
  };
}

describe('Synthetic energy analytics', () => {
  let http: HttpTestingController;
  beforeEach(() => {
    window.__AEROSYNAPSE_CONFIG__ = { apiBaseUrl: '/api', intelligenceBaseUrl: '/intelligence/' };
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([apiErrorInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => {
    http.verify();
    delete window.__AEROSYNAPSE_CONFIG__;
  });

  it('posts separate immutable snapshots and renders energy and traffic from the API together', () => {
    const fixture = TestBed.createComponent(EnergyAnalytics);
    const component = fixture.componentInstance;
    component.compare();
    component.optimize();
    const compare = http.expectOne('/intelligence/energy/v1/compare');
    const optimize = http.expectOne('/intelligence/energy/v1/optimize');
    expect(compare.request.method).toBe('POST');
    const body = compare.request.body as EnergyRequest;
    expect(body).not.toBe(component.draft);
    expect(optimize.request.body).not.toBe(body);
    expect(optimize.request.body).toEqual(body);
    component.draft.checkpoints[0].label = 'Later local edit';
    expect(body.checkpoints[0].label).toBe('Checkpoint sintetic A');
    const response = comparison(body);
    compare.flush(response);
    optimize.flush(optimization(body));
    response.candidate.energy.totalKwh = 999;
    fixture.detectChanges();
    const element: HTMLElement = fixture.nativeElement;
    expect(element.querySelector('[data-testid="energy-results"]')?.textContent).toContain('3.950');
    expect(element.textContent).toContain('245');
    expect(element.textContent).toContain('Ipoteză implicită · coeficient lipsă');

    expect(component.comparing()).toBe(false);
    expect(component.optimizing()).toBe(false);
    expect(component.comparison()?.candidate.energy.totalKwh).toBe(3.95);
  });

  it('distinguishes an empty coefficient and tariff from configured zero in typed number inputs', async () => {
    const fixture = TestBed.createComponent(EnergyAnalytics);
    fixture.detectChanges();
    await fixture.whenStable();
    const input: HTMLInputElement = fixture.nativeElement.querySelector(
      'input[name="checkpointPower"]',
    );
    input.value = '0';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    await fixture.whenStable();
    expect(fixture.componentInstance.draft.checkpointPowerKw).toBe(0);
    expect(input.closest('label')?.textContent).toContain('Ipoteză configurată');
    input.value = '';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    await fixture.whenStable();
    expect(fixture.componentInstance.draft.checkpointPowerKw).toBeNull();
    expect(input.closest('label')?.textContent).toContain('Lipsă → ipoteză implicită');
    fixture.componentInstance.draft.tariffRonPerKwh = 0;
    fixture.componentInstance.compare();
    const pending = http.expectOne('/intelligence/energy/v1/compare');
    expect(pending.request.body.tariffRonPerKwh).toBe(0);
    pending.flush(comparison(pending.request.body));
  });

  it('renders missing tariff and null waiting times without presenting them as zero', () => {
    const fixture = TestBed.createComponent(EnergyAnalytics);
    fixture.componentInstance.draft.tariffRonPerKwh = null;
    fixture.componentInstance.compare();
    const pending = http.expectOne('/intelligence/energy/v1/compare');
    const response = comparison(pending.request.body);
    response.baseline.traffic.meanWaitMinutes = null;
    response.baseline.traffic.p95WaitMinutes = null;
    pending.flush(response);
    fixture.detectChanges();
    const text = fixture.nativeElement.textContent;
    expect(text).toContain('Indisponibil · tarif lipsă');
    expect(text).toContain('N/A · niciun procesat');
  });

  it('clears stale results and cancels both requests immediately after a configuration edit', () => {
    const fixture = TestBed.createComponent(EnergyAnalytics);
    const component = fixture.componentInstance;
    component.compare();
    component.optimize();
    const compare = http.expectOne('/intelligence/energy/v1/compare');
    const optimize = http.expectOne('/intelligence/energy/v1/optimize');
    component.draft.energyBudgetKwh = 0;
    component.changed();
    expect(compare.cancelled).toBe(true);
    expect(optimize.cancelled).toBe(true);
    expect(component.comparison()).toBeNull();
    expect(component.optimization()).toBeNull();
    expect(component.notice()).toContain('Configurația s-a schimbat');
  });

  it('retains each independent result when the other request fails', () => {
    const fixture = TestBed.createComponent(EnergyAnalytics);
    fixture.componentInstance.compare();
    fixture.componentInstance.optimize();
    http.expectOne('/intelligence/energy/v1/compare').flush(comparison());
    http
      .expectOne('/intelligence/energy/v1/optimize')
      .flush({ detail: 'Grid failed' }, { status: 503, statusText: 'Unavailable' });
    expect(fixture.componentInstance.comparison()).not.toBeNull();
    expect(fixture.componentInstance.optimizerError()).not.toBe('');
    expect(fixture.componentInstance.optimizing()).toBe(false);
    fixture.componentInstance.compare();
    fixture.componentInstance.optimize();
    http.expectOne('/intelligence/energy/v1/optimize').flush(optimization());
    http
      .expectOne('/intelligence/energy/v1/compare')
      .flush({}, { status: 500, statusText: 'Failed' });
    expect(fixture.componentInstance.optimization()).not.toBeNull();
    expect(fixture.componentInstance.error()).not.toBe('');
  });

  it('blocks oversized optimizer grids while allowing the independent comparison', () => {
    const fixture = TestBed.createComponent(EnergyAnalytics);
    const component = fixture.componentInstance;
    component.addCheckpoint();
    component.addCheckpoint();
    for (const row of component.draft.checkpoints) row.maxUnits = 8;
    fixture.detectChanges();
    expect(component.gridCount()).toBe(729);
    expect(fixture.nativeElement.querySelector('[data-testid="energy-optimize"]').disabled).toBe(
      true,
    );
    component.optimize();
    http.expectNone('/intelligence/energy/v1/optimize');
    expect(component.optimizerError()).toContain('128');
    component.compare();
    http.expectOne('/intelligence/energy/v1/compare').flush(comparison(component.draft));
  });

  it('shows an infeasible zero budget with backend provenance and formulas and no claimed solution', () => {
    const fixture = TestBed.createComponent(EnergyAnalytics);
    const component = fixture.componentInstance;
    component.draft.energyBudgetKwh = 0;
    component.optimize();
    const pending = http.expectOne('/intelligence/energy/v1/optimize');
    expect(pending.request.body.energyBudgetKwh).toBe(0);
    const response = {
      ...optimization(),
      feasibleCount: 0,
      status: 'infeasible',
      best: null,
      candidates: [],
      assumptions: ['Full backend provenance, including infeasible configurations.'],
      formulas: {
        energy: 'backend energy formula',
        cost: 'backend cost formula',
        sensitivity: 'backend OAT formula',
      },
    };
    pending.flush(response);
    expect(component.optimization()?.effectiveCoefficients).toEqual(response.effectiveCoefficients);
    expect(component.optimization()?.assumptions).toEqual(response.assumptions);
    expect(component.optimization()?.formulas).toEqual(response.formulas);
    expect(component.optimization()?.best).toBeNull();
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('NEFEZABIL ÎN GRILĂ');
    expect(fixture.nativeElement.textContent).toContain('backend energy formula');
    expect(fixture.nativeElement.textContent).toContain('Full backend provenance');
    expect(fixture.nativeElement.textContent).toContain('Ipoteză implicită · coeficient lipsă');
  });

  it('refuses server data without a synthetic assertion or matching population fingerprint', () => {
    const fixture = TestBed.createComponent(EnergyAnalytics);
    const component = fixture.componentInstance;
    component.compare();
    http.expectOne('/intelligence/energy/v1/compare').flush({ ...comparison(), synthetic: false });
    expect(component.comparison()).toBeNull();
    expect(component.error()).toContain('sintetic');
    component.compare();
    const response = comparison();
    response.candidate.populationFingerprint = 'different';
    http.expectOne('/intelligence/energy/v1/compare').flush(response);
    expect(component.comparison()).toBeNull();
    expect(component.error()).toContain('Populațiile diferă');
  });
});

describe('Energy input contract', () => {
  it('uses fresh synthetic demos and correctly sizes the bounded optimizer grid', () => {
    const first = energyDemoRequest();
    first.loads[0].units = 0;
    const second = energyDemoRequest();
    expect(second.loads[0].units).toBe(20);
    expect(energyGridCount(second)).toBe(4);
    expect(validateEnergy(second)).toEqual([]);
  });
  it('accepts zero as configured power, cost, budget, population, units and active minutes', () => {
    const request = energyDemoRequest();
    request.checkpointPowerKw = 0;
    request.tariffRonPerKwh = 0;
    request.energyBudgetKwh = 0;
    request.passengerCount = 0;
    request.arrivalRatePerMinute = 0;
    request.checkpoints[0].baselineUnits = 0;
    request.checkpoints[0].candidateUnits = 0;
    request.loads[0].units = 0;
    request.loads[0].powerKw = 0;
    request.loads[0].candidateActiveMinutes = 0;
    expect(validateEnergy(request)).toEqual([]);
  });
  it('rejects missing required values, fractions of units, out-of-horizon activity and reserved load IDs', () => {
    const request = energyDemoRequest();
    request.durationMinutes = null as unknown as number;
    request.checkpoints[0].baselineUnits = 1.5;
    request.loads[0].id = 'checkpointPowerKw';
    request.loads[0].candidateActiveMinutes = 1000;
    const errors = validateEnergy(request).join(' ');
    expect(errors).toContain('Orizont');
    expect(errors).toContain('unități întregi');
    expect(errors).toContain('identificatori unici');
    expect(errors).toContain('minutele active');
  });
});
