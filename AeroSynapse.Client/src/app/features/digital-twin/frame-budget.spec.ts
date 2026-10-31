import { FrameBudget, MVP_MIN_FPS } from './frame-budget';

describe('MVP frame budget', () => {
  function sample(budget: FrameBudget, fps: number, start = 0) {
    let latest;
    for (let t = start; t <= start + 5000; t += 1000 / fps) latest = budget.record(t) ?? latest;
    return latest!;
  }
  it('measures rendered frames after startup without reducing a healthy scene', () => {
    const budget = new FrameBudget(2);
    const status = sample(budget, 60);
    expect(status.fps).toBeCloseTo(60);
    expect(status.fps).toBeGreaterThanOrEqual(MVP_MIN_FPS);
    expect(status.sampleSeconds).toBeGreaterThanOrEqual(2.5);
    expect(status.frameP95Ms).toBeCloseTo(16.7);
    expect(status.state).toBe('ok');
    expect(budget.pixelRatio).toBe(1.5);
  });
  it('reduces quality before choosing 2D after two slow windows at minimum quality', () => {
    const budget = new FrameBudget(2);
    const states = [];
    for (let t = 0; t < 14000; t += 50) {
      const status = budget.record(t);
      if (status) states.push(status.state);
    }
    expect(states).toEqual(['adjusting', 'adjusting', 'low', 'fallback']);
    expect(budget.pixelRatio).toBe(0.75);
  });
  it('excludes a hidden-tab gap when sampling resumes', () => {
    const budget = new FrameBudget(1);
    sample(budget, 60);
    budget.resetSample();
    expect(sample(budget, 60, 60000).fps).toBeCloseTo(60);
    expect(budget.pixelRatio).toBe(1);
  });
});
