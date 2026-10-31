import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ServiceStatus } from './service-status';

describe('Backend connectivity through the frontend', () => {
  beforeEach(() =>
    TestBed.configureTestingModule({
      imports: [ServiceStatus],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }),
  );
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('reports both services when their proxied HTTP requests succeed', async () => {
    const fixture = TestBed.createComponent(ServiceStatus);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    expect(fixture.nativeElement.textContent).toContain('verificare');
    http.expectOne('/api/health').flush({ status: 'Healthy' });
    http.expectOne('/intelligence/health').flush({ status: 'Healthy' });
    await fixture.whenStable();
    expect(fixture.nativeElement.textContent).toContain('API · conectat');
    expect(fixture.nativeElement.textContent).toContain('FastAPI · conectat');
  });

  it('keeps the UI functional when a backend is unavailable', async () => {
    const fixture = TestBed.createComponent(ServiceStatus);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/health').flush({}, { status: 502, statusText: 'Bad Gateway' });
    http.expectOne('/intelligence/health').flush({ status: 'Healthy' });
    await fixture.whenStable();
    expect(fixture.nativeElement.textContent).toContain('API · indisponibil');
    expect(fixture.nativeElement.textContent).toContain('FastAPI · conectat');
  });
});
