import { TestBed } from '@angular/core/testing';
import { App } from './app';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

describe('App', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
  });

  it('should create the app', () => {
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    TestBed.inject(HttpTestingController).expectOne('/api/health').flush({ status: 'Healthy' });
    TestBed.inject(HttpTestingController)
      .expectOne('/intelligence/health')
      .flush({ status: 'Healthy' });
    const app = fixture.componentInstance;
    expect(app).toBeTruthy();
  });

  it('offers both feature routes in the application shell', async () => {
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    TestBed.inject(HttpTestingController).expectOne('/api/health').flush({ status: 'Healthy' });
    TestBed.inject(HttpTestingController)
      .expectOne('/intelligence/health')
      .flush({ status: 'Healthy' });
    await fixture.whenStable();
    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.querySelector('a[href="/control-tower"]')).toBeTruthy();
    expect(compiled.querySelector('a[href="/scenario"]')).toBeTruthy();
  });
});
