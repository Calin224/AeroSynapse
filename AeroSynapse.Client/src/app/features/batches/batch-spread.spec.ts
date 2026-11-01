import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { BATCH_METRICS, SpreadLane } from './batch-insight';
import { BatchSpread } from './batch-spread';

const lane = (variantId: string, position: number, values: number[]): SpreadLane => ({
  variantId,
  name: position ? 'Mai mult personal' : 'Pornire',
  position,
  points: values.map((value, index) => ({
    id: `${variantId}-${index}`,
    runId: `run-${variantId}-${index}`,
    day: index + 1,
    seed: 42 + index,
    value,
  })),
  stats: {
    count: values.length,
    mean: 0,
    standardDeviation: null,
    minimum: Math.min(...values),
    maximum: Math.max(...values),
    median: [...values].sort((a, b) => a - b)[Math.floor(values.length / 2)],
    p05: Math.min(...values),
    p95: Math.max(...values),
  },
});

describe('batch spread chart', () => {
  function chart(lanes = [lane('a', 0, [10, 20, 30, 40]), lane('b', 1, [5, 6])]) {
    TestBed.configureTestingModule({ providers: [provideRouter([])] });
    const fixture = TestBed.createComponent(BatchSpread);
    fixture.componentRef.setInput('lanes', lanes);
    fixture.componentRef.setInput('metric', BATCH_METRICS[0]);
    fixture.componentRef.setInput('unit', 'minutes');
    fixture.detectChanges();
    return fixture;
  }
  const dots = (element: HTMLElement) =>
    [...element.querySelectorAll<HTMLButtonElement>('.dot')].map((dot) =>
      dot.getAttribute('aria-label'),
    );

  it('draws one labelled dot per simulated day with a single tab stop', () => {
    const element: HTMLElement = chart().nativeElement;
    expect(dots(element)).toEqual([
      'Scenariul de pornire, ziua 1: 10.00 min',
      'Scenariul de pornire, ziua 2: 20.00 min',
      'Scenariul de pornire, ziua 3: 30.00 min',
      'Scenariul de pornire, ziua 4: 40.00 min, dincolo de prag',
      'Varianta 1, ziua 1: 5.00 min',
      'Varianta 1, ziua 2: 6.00 min',
    ]);
    expect(element.querySelectorAll('.dot[tabindex="0"]')).toHaveLength(1);
    expect(element.querySelector('.plot')!.getAttribute('aria-label')).toContain(
      'Spre stânga e mai bine',
    );
  });

  it('walks the days with the arrow keys and describes the focused day', () => {
    const fixture = chart();
    const element: HTMLElement = fixture.nativeElement;
    const first = element.querySelector<HTMLButtonElement>('.dot')!;
    first.focus();
    fixture.detectChanges();
    expect(element.querySelector('.caption')!.textContent).toContain('Cea mai bună zi din 4');
    const press = (key: string) => {
      document.activeElement!.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true }));
      fixture.detectChanges();
      return document.activeElement?.getAttribute('aria-label');
    };
    expect(press('ArrowRight')).toBe('Scenariul de pornire, ziua 2: 20.00 min');
    expect(press('End')).toBe('Scenariul de pornire, ziua 4: 40.00 min, dincolo de prag');
    expect(element.querySelector('.caption')!.textContent).toContain('Cea mai grea zi din 4');
    expect(press('ArrowDown')).toBe('Varianta 1, ziua 2: 6.00 min');
    expect(press('ArrowUp')).toBe('Scenariul de pornire, ziua 1: 10.00 min');
    expect(element.querySelector('.dot.on')!.getAttribute('tabindex')).toBe('0');
    expect(element.querySelector('.caption a')!.getAttribute('href')).toBe('/replay?runId=run-a-0');
  });

  it('offers every value as a table and waits politely for days not yet simulated', () => {
    const element: HTMLElement = chart([
      lane('a', 0, [12.34]),
      { ...lane('b', 1, [1]), points: [] },
    ]).nativeElement;
    const rows = [...element.querySelectorAll('tbody tr')].map((row) =>
      [...row.children].map((cell) => cell.textContent?.trim()),
    );
    expect(rows).toEqual([
      ['Scenariul de pornire · Pornire', '1', 'ziua 1: 12.34 min'],
      ['Varianta 1 · Mai mult personal', '0', '—'],
    ]);
    expect(element.textContent).toContain('Nicio zi finalizată.');
  });
});
