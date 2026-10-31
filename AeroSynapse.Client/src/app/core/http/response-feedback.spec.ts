import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { API_BASE_URL } from './api-client';
import {
  ResponseFeedback,
  responseFeedbackInterceptor,
  SLOW_RESPONSE_MS,
} from './response-feedback';

describe('Measured API response feedback', () => {
  let http: HttpTestingController, client: HttpClient, feedback: ResponseFeedback;
  beforeEach(() => {
    vi.useFakeTimers();
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([responseFeedbackInterceptor])),
        provideHttpClientTesting(),
        { provide: API_BASE_URL, useValue: '/api' },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    client = TestBed.inject(HttpClient);
    feedback = TestBed.inject(ResponseFeedback);
  });
  afterEach(() => {
    http.verify();
    TestBed.resetTestingModule();
    vi.useRealTimers();
    vi.restoreAllMocks();
  });
  it('measures concurrent requests and clears slow feedback after cancellation and completion', () => {
    let now = 100;
    vi.spyOn(performance, 'now').mockImplementation(() => now);
    const first = client.get('/api/scenarios').subscribe();
    const read = http.expectOne('/api/scenarios');
    now += SLOW_RESPONSE_MS;
    vi.advanceTimersByTime(SLOW_RESPONSE_MS);
    expect(feedback.slow()).toBe(true);
    client.post('/api/commands', {}).subscribe();
    expect(feedback.pending()).toBe(2);
    first.unsubscribe();
    expect(read.cancelled).toBe(true);
    expect(feedback.slow()).toBe(false);
    now += 50;
    http.expectOne('/api/commands').flush({ accepted: true });
    expect(feedback.pending()).toBe(0);
    expect(vi.getTimerCount()).toBe(0);
  });
  it('cleans up after server and transport errors without suppressing them or retrying commands', () => {
    const error = vi.fn();
    client.post('/api/commands', {}).subscribe({ error });
    http.expectOne('/api/commands').error(new ProgressEvent('error'));
    expect(error).toHaveBeenCalledOnce();
    expect(feedback.pending()).toBe(0);
    expect(vi.getTimerCount()).toBe(0);
    window.dispatchEvent(new Event('online'));
    http.expectNone('/api/commands');
    client.get('/api/scenarios').subscribe({ error });
    http.expectOne('/api/scenarios').flush({}, { status: 503, statusText: 'Unavailable' });
    expect(error).toHaveBeenCalledTimes(2);
    expect(feedback.pending()).toBe(0);
    expect(vi.getTimerCount()).toBe(0);
  });
  it('does not count visual assets or other services as API responses', () => {
    client.get('/assets/terminal.glb').subscribe();
    expect(feedback.pending()).toBe(0);
    http.expectOne('/assets/terminal.glb').flush({});
    expect(vi.getTimerCount()).toBe(0);
  });
});
