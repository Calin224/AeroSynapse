import { TestBed } from '@angular/core/testing';
import { DigitalTwin, TWIN_RENDERER_FACTORY } from './digital-twin';
import { TwinRenderer } from './twin-model';

describe('DigitalTwin component boundary', () => {
  it('updates its renderer and disposes it when the component is destroyed', async () => {
    const renderer: TwinRenderer = {
      setScene: vi.fn(),
      resetCamera: vi.fn(),
      dispose: vi.fn(),
      applySurface: vi.fn(),
      resetSurface: vi.fn(),
      getAppliedSurface: vi.fn(() => null),
    };
    TestBed.configureTestingModule({
      imports: [DigitalTwin],
      providers: [{ provide: TWIN_RENDERER_FACTORY, useValue: () => renderer }],
    });
    const fixture = TestBed.createComponent(DigitalTwin);
    fixture.componentRef.setInput('scene', { aircraft: [] });
    await fixture.whenStable();
    expect(renderer.setScene).toHaveBeenCalledWith({ aircraft: [] });
    const nextScene = { aircraft: [{ id: 'test', x: 1, z: 2, heading: 90 }] };
    fixture.componentRef.setInput('scene', nextScene);
    await fixture.whenStable();
    expect(renderer.setScene).toHaveBeenLastCalledWith(nextScene);
    (fixture.nativeElement as HTMLElement).querySelector('button')?.click();
    expect(renderer.resetCamera).toHaveBeenCalledOnce();
    fixture.destroy();
    expect(renderer.dispose).toHaveBeenCalledOnce();
  });
  it('shows a recoverable error when WebGL initialization fails', async () => {
    const renderer = {
      setScene: vi.fn(),
      resetCamera: vi.fn(),
      dispose: vi.fn(),
      applySurface: vi.fn(),
      resetSurface: vi.fn(),
      getAppliedSurface: vi.fn(() => null),
    };
    const factory = vi
      .fn()
      .mockImplementationOnce(() => {
        throw Error('WebGL unavailable');
      })
      .mockReturnValue(renderer);
    TestBed.configureTestingModule({
      imports: [DigitalTwin],
      providers: [{ provide: TWIN_RENDERER_FACTORY, useValue: factory }],
    });
    const fixture = TestBed.createComponent(DigitalTwin);
    fixture.componentRef.setInput('scene', { aircraft: [] });
    await fixture.whenStable();
    expect(fixture.nativeElement.textContent).toContain('Scena 3D nu este disponibilă');
    (fixture.nativeElement as HTMLElement).querySelector('button')?.click();
    await fixture.whenStable();
    expect(factory).toHaveBeenCalledTimes(2);
    expect(fixture.nativeElement.textContent).toContain('Resetează camera');
  });
});
