import { TwinPerformance } from './twin-model';

/** MVP: >=30 rendered FPS over 2.5 s; exclude startup and hidden/paused tabs. */
export const MVP_MIN_FPS = 30;
export const MVP_SAMPLE_MS = 2500;
export class FrameBudget {
  private previous?: number;
  private warmupUntil?: number;
  private intervals: number[] = [];
  private level = 0;
  private lowWindows = 0;
  constructor(private readonly deviceRatio: number) {}
  get pixelRatio(): number {
    return Math.min(this.deviceRatio, [1.5, 1, 0.75][this.level]);
  }
  resetSample(): void {
    this.previous = undefined;
    this.warmupUntil = undefined;
    this.intervals = [];
    this.lowWindows = 0;
  }
  record(time: number): TwinPerformance | undefined {
    if (this.previous === undefined) {
      this.previous = time;
      this.warmupUntil = time + 2000;
      return;
    }
    const elapsed = time - this.previous;
    this.previous = time;
    if (time < this.warmupUntil! || elapsed <= 0) return;
    this.intervals.push(elapsed);
    const duration = this.intervals.reduce((sum, value) => sum + value, 0);
    if (duration < MVP_SAMPLE_MS) return;
    const fps = (this.intervals.length * 1000) / duration;
    const sorted = [...this.intervals].sort((a, b) => a - b);
    const status: TwinPerformance = {
      fps: Math.round(fps * 10) / 10,
      frameP95Ms: Math.round(sorted[Math.ceil(sorted.length * 0.95) - 1] * 10) / 10,
      sampleFrames: sorted.length,
      sampleSeconds: Math.round(duration) / 1000,
      pixelRatio: this.pixelRatio,
      quality: (['high', 'balanced', 'low'] as const)[this.level],
      state: 'ok',
    };
    this.intervals = [];
    if (fps < MVP_MIN_FPS) {
      if (this.level < 2) {
        ++this.level;
        status.state = 'adjusting';
      } else status.state = ++this.lowWindows >= 2 ? 'fallback' : 'low';
    } else this.lowWindows = 0;
    return status;
  }
}
