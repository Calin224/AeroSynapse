import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ApiClient, API_BASE_URL } from './api-client';
import { ApiError, apiErrorInterceptor } from './api-error.interceptor';

describe('HTTP infrastructure', () => {
  let http: HttpTestingController;
  let api: ApiClient;
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([apiErrorInterceptor])),
        provideHttpClientTesting(),
        { provide: API_BASE_URL, useValue: 'https://api.example.test/' },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    api = TestBed.inject(ApiClient);
  });
  afterEach(() => http.verify());
  it('joins the configured base URL and relative path', () => {
    const result = vi.fn();
    api.get('/scenarios').subscribe(result);
    http.expectOne('https://api.example.test/scenarios').flush([]);
    expect(result).toHaveBeenCalledWith([]);
  });
  it('preserves every validation field for editors while keeping the summary concise', () => {
    const onError = vi.fn();
    api.post('scenarios/validate', {}).subscribe({ error: onError });
    const errors = Object.fromEntries(
      Array.from({ length: 8 }, (_, index) => [
        `inputs.flights[${index}].gateId`,
        ['Gate unavailable.'],
      ]),
    );
    http
      .expectOne('https://api.example.test/scenarios/validate')
      .flush({ code: 'validation_failed', errors }, { status: 400, statusText: 'Invalid' });
    const error = onError.mock.calls[0][0] as ApiError;
    expect(error.fieldErrors).toEqual(errors);
    expect(error.code).toBe('validation_failed');
    expect(error.message).not.toContain('flights[7]');
  });
  it('preserves versioned contract error paths without exposing arbitrary server details', () => {
    const onError = vi.fn();
    api.post('scenarios/validate', {}).subscribe({ error: onError });
    http.expectOne('https://api.example.test/scenarios/validate').flush(
      {
        errors: [
          {
            field: '/inputs/navigation/graph/nodes/0/id',
            code: 'required',
            message: 'Required.',
          },
        ],
      },
      { status: 422, statusText: 'Invalid' },
    );
    expect((onError.mock.calls[0][0] as ApiError).fieldErrors).toEqual({
      '/inputs/navigation/graph/nodes/0/id': ['Required.'],
    });
  });
  it.each([0, 404, 500])('normalizes status %s without leaking server details', (status) => {
    const onError = vi.fn();
    api.get('scenarios').subscribe({ error: onError });
    const request = http.expectOne('https://api.example.test/scenarios');
    if (status === 0) request.error(new ProgressEvent('error'));
    else request.flush({ secret: 'server details' }, { status, statusText: 'Failure' });
    const error = onError.mock.calls[0][0] as ApiError;
    expect(error).toBeInstanceOf(ApiError);
    expect(error.status).toBe(status);
    expect(error.message).not.toContain('server details');
  });
  it('identifies unavailable staffing planning without exposing server details', () => {
    const onError = vi.fn();
    api.post('scenarios/scenario-1/staffing/proposals', {}).subscribe({ error: onError });
    http.expectOne('https://api.example.test/scenarios/scenario-1/staffing/proposals').flush(
      { code: 'staffing_planner_unavailable', detail: 'server details' }, { status: 502, statusText: 'Bad Gateway' });
    const error = onError.mock.calls[0][0] as ApiError;
    expect(error.code).toBe('staffing_planner_unavailable');
    expect(error.message).toContain('optimizare a personalului');
    expect(error.message).not.toContain('server details');
  });
});
