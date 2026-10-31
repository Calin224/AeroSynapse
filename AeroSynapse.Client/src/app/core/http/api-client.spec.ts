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
    api.get('/data/scenarios.json').subscribe(result);
    http.expectOne('https://api.example.test/data/scenarios.json').flush([]);
    expect(result).toHaveBeenCalledWith([]);
  });
  it.each([0, 404, 500])('normalizes status %s without leaking server details', (status) => {
    const onError = vi.fn();
    api.get('data/scenarios.json').subscribe({ error: onError });
    const request = http.expectOne('https://api.example.test/data/scenarios.json');
    if (status === 0) request.error(new ProgressEvent('error'));
    else request.flush({ secret: 'server details' }, { status, statusText: 'Failure' });
    const error = onError.mock.calls[0][0] as ApiError;
    expect(error).toBeInstanceOf(ApiError);
    expect(error.status).toBe(status);
    expect(error.message).not.toContain('server details');
  });
});
