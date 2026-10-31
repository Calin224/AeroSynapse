import { TestBed } from '@angular/core/testing';
import { App } from './app';
import { SessionState } from './core/auth/session-state';
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

  it('offers the feature routes in the application header', async () => {
    TestBed.inject(SessionState).session.set({
      username: 'navigation-test',
      roles: ['Operator'],
      expiresAtUtc: '2099-01-01T00:00:00Z',
    });
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    TestBed.inject(HttpTestingController).expectNone('/api/health');
    TestBed.inject(HttpTestingController).expectNone('/intelligence/health');
    await fixture.whenStable();
    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.querySelector('a[href="/control-tower"]')).toBeTruthy();
    expect(
      compiled.querySelector('app-header nav a[href="/control-tower/configure"]'),
    ).toBeTruthy();
    expect(compiled.querySelector('app-header nav a[href="/operations"]')).toBeNull();
    expect(compiled.querySelector('app-header nav a[href="/control-tower/batches"]')).toBeTruthy();
    expect(compiled.querySelector('app-header nav a[href="/control-tower/compare"]')).toBeTruthy();
    expect(compiled.querySelector('app-header nav a[href="/passenger"]')).toBeNull();
    expect(compiled.querySelector('app-header nav a[href="/evacuation-training"]')).toBeNull();
  });
});
