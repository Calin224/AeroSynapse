import {
  afterNextRender,
  Component,
  DestroyRef,
  effect,
  ElementRef,
  inject,
  InjectionToken,
  input,
  signal,
  viewChild,
} from '@angular/core';
import { ButtonModule } from 'primeng/button';
import { DataState } from '../../shared/ui/data-state';
import { ThreeTwinRenderer } from './three-twin-renderer';
import { TwinRenderer, TwinScene } from './twin-model';

export const TWIN_RENDERER_FACTORY = new InjectionToken<
  (host: HTMLElement, onError: () => void) => TwinRenderer
>('TWIN_RENDERER_FACTORY', {
  providedIn: 'root',
  factory: () => (host, onError) => new ThreeTwinRenderer(host, onError),
});

@Component({
  selector: 'app-digital-twin',
  imports: [ButtonModule, DataState],
  template: ` <div class="viewport" [attr.aria-busy]="status() === 'loading'">
    <div #host class="canvas-host" [class.hidden]="status() === 'error'"></div>
    @if (status() === 'loading') {
      <div class="overlay"><app-data-state status="loading" label="scena 3D" /></div>
    }
    @if (status() === 'error') {
      <div class="overlay">
        <app-data-state
          status="error"
          message="Scena 3D nu este disponibilă. Verifică suportul WebGL și accelerarea hardware a browserului."
          (retry)="initialize()"
        />
      </div>
    }
    @if (status() === 'ready') {
      <div class="scene-label"><span class="status-dot"></span> CRA / AIRPORT MODEL</div>
      <div class="camera-action">
        <p-button
          label="Resetează camera"
          icon="pi pi-compass"
          [outlined]="true"
          size="small"
          (onClick)="resetCamera()"
        />
      </div>
      <div class="legend">
        <span><b class="airport-key"></b> Infrastructură</span
        ><span><b class="aircraft-key"></b> Aeronave</span>
      </div>
    }
  </div>`,
  styles: `
    :host {
      display: block;
    }
    .viewport {
      position: relative;
      height: 440px;
      background: #0e1b27;
    }
    .canvas-host {
      width: 100%;
      height: 100%;
      overflow: hidden;
      touch-action: none;
    }
    .hidden {
      visibility: hidden;
    }
    .overlay {
      position: absolute;
      inset: 0;
      display: grid;
      align-items: center;
    }
    .scene-label {
      position: absolute;
      top: 20px;
      left: 20px;
      font-size: 9px;
      letter-spacing: 1.4px;
      color: #a8c4ce;
      pointer-events: none;
    }
    .camera-action {
      position: absolute;
      top: 16px;
      right: 16px;
    }
    .legend {
      position: absolute;
      bottom: 20px;
      left: 20px;
      display: flex;
      gap: 18px;
      font-size: 10px;
      color: #a8c4ce;
      pointer-events: none;
    }
    b {
      display: inline-block;
      width: 6px;
      height: 6px;
      margin-right: 6px;
      border-radius: 2px;
    }
    .airport-key {
      background: #658696;
    }
    .aircraft-key {
      background: #e8f2f4;
    }
    @media (max-width: 650px) {
      .viewport {
        height: 340px;
      }
      .scene-label {
        top: 68px;
      }
    }
  `,
})
export class DigitalTwin {
  readonly scene = input.required<TwinScene>();
  protected readonly status = signal<'loading' | 'ready' | 'error'>('loading');
  private readonly host = viewChild.required<ElementRef<HTMLElement>>('host');
  private readonly createRenderer = inject(TWIN_RENDERER_FACTORY);
  private renderer?: TwinRenderer;

  constructor() {
    afterNextRender(() => this.initialize());
    effect(() => {
      const scene = this.scene();
      this.renderer?.setScene(scene);
    });
    inject(DestroyRef).onDestroy(() => this.releaseRenderer());
  }

  protected initialize(): void {
    this.releaseRenderer();
    this.status.set('loading');
    try {
      this.renderer = this.createRenderer(this.host().nativeElement, () => this.fail());
      this.renderer.setScene(this.scene());
      if (this.renderer) this.status.set('ready');
    } catch {
      this.fail();
    }
  }

  protected resetCamera(): void {
    this.renderer?.resetCamera();
  }
  private fail(): void {
    this.releaseRenderer();
    this.status.set('error');
  }
  private releaseRenderer(): void {
    const renderer = this.renderer;
    this.renderer = undefined;
    renderer?.dispose();
  }
}
