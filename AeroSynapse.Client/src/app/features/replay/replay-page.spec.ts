import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { ReplayPage } from './replay-page';
import { ids, replayData, replayEnvelope } from './replay.testing';
import { TWIN_RENDERER_FACTORY } from '../digital-twin/digital-twin';

describe('Server replay lifecycle', () => {
  let http: HttpTestingController;
  beforeEach(async () => {
    vi.stubGlobal(
      'crypto',
      (await vi.importActual<{ webcrypto: Crypto }>('node:crypto')).webcrypto,
    );
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        {
          provide: TWIN_RENDERER_FACTORY,
          useValue: () => ({
            setScene: vi.fn(),
            setHeatmap: vi.fn(),
            setPassengers: vi.fn(),
            resetCamera: vi.fn(),
            dispose: vi.fn(),
          }),
        },
      ],
    });
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => {
    http
      .match('/api/scenarios')
      .filter((r) => !r.cancelled)
      .forEach((r) => r.flush([]));
    http.verify();
    vi.unstubAllGlobals();
  });
  it('loads validated server data without offline controls or bookmark actions', async () => {
    const fixture = TestBed.createComponent(ReplayPage);
    fixture.componentInstance.open(ids.run);
    expect(fixture.componentInstance.busy()).toBe(true);
    http.expectOne(`/api/runs/${ids.run}/replay`).flush(await replayEnvelope());
    await vi.waitFor(() => expect(fixture.componentInstance.prepared()?.data.run.id).toBe(ids.run));
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('app-replay-player')).toBeTruthy();
    expect(
      fixture.nativeElement.querySelector('.bookmarks-panel, .offline-tools, .run-context'),
    ).toBeNull();
    http.expectNone((r) => r.url.includes('/bookmarks'));
    fixture.destroy();
  });
  it('cancels a previous fetch and ignores data that finishes validation after selection changes', async () => {
    const fixture = TestBed.createComponent(ReplayPage),
      view = fixture.componentInstance;
    view.open(ids.run);
    const previous = http.expectOne(`/api/runs/${ids.run}/replay`);
    view.open(ids.otherRun);
    expect(previous.cancelled).toBe(true);
    http
      .expectOne(`/api/runs/${ids.otherRun}/replay`)
      .flush(await replayEnvelope(replayData(ids.otherRun)));
    view.open(ids.run);
    http.expectOne(`/api/runs/${ids.run}/replay`).flush(await replayEnvelope());
    await vi.waitFor(() => expect(view.prepared()?.data.run.id).toBe(ids.run));
    expect(view.busy()).toBe(false);
    fixture.destroy();
  });
  it('rejects corrupted payloads and retries through the server after a fetch error', async () => {
    const fixture = TestBed.createComponent(ReplayPage),
      view = fixture.componentInstance;
    const envelope = await replayEnvelope();
    view.open(ids.run);
    http
      .expectOne(`/api/runs/${ids.run}/replay`)
      .flush({ ...envelope, payloadJson: envelope.payloadJson + ' ' });
    await vi.waitFor(() => expect(view.message()).toContain('Amprenta'));
    expect(view.prepared()).toBeNull();
    view.open(ids.run);
    http
      .expectOne(`/api/runs/${ids.run}/replay`)
      .flush({}, { status: 503, statusText: 'Unavailable' });
    expect(view.busy()).toBe(false);
    expect(view.message()).toBeTruthy();
    view.open(ids.run);
    http.expectOne(`/api/runs/${ids.run}/replay`).flush(envelope);
    await vi.waitFor(() => expect(view.prepared()).not.toBeNull());
    expect(view.message()).toBe('');
    fixture.destroy();
  });
  it('rejects invalid identifiers and cancels a pending request on destroy', () => {
    const fixture = TestBed.createComponent(ReplayPage),
      view = fixture.componentInstance;
    view.open('invalid');
    http.expectNone((r) => r.url.includes('/replay'));
    expect(view.busy()).toBe(false);
    view.open(ids.run);
    const request = http.expectOne(`/api/runs/${ids.run}/replay`);
    fixture.destroy();
    expect(request.cancelled).toBe(true);
  });
});
