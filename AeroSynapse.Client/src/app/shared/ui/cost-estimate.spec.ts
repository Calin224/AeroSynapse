import { costText, netSaving } from './cost-estimate';

describe('Cost estimate', () => {
  it('values passenger time saved minus operator hours added, at the sourced rates', () => {
    // 10 h × 22.89 = 228.9; 2 h freed × 12.5 = 25 back.
    expect(netSaving(10, -2)).toBeCloseTo(253.9);
    expect(costText(253.9)).toBe('Economie estimată: 254 €');
    expect(costText(-445.4)).toBe('Cost estimat: 445 €');
  });
});
