import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { ids, towerFixture } from '../../control-tower/tower.testing';
import {
  alertFixture,
  activityFixture,
  detailFixture,
  notificationIds,
  notificationRunFixture,
} from '../notification.testing';
import { NotificationCenterComponent } from './notification-center.component';

describe('Supervisor notification UI', () => {
  let http: HttpTestingController;
  beforeEach(() => {
    vi.useFakeTimers();
    vi.stubGlobal('localStorage', { getItem: () => null, setItem: vi.fn(), removeItem: vi.fn() });
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    });
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => {
    TestBed.resetTestingModule();
    http.verify({ ignoreCancelled: true });
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });
  async function view() {
    const fixture = TestBed.createComponent(NotificationCenterComponent);
    fixture.detectChanges();
    http.expectOne('/api/scenarios').flush([towerFixture().scenario.scenario]);
    // Replace the all-scenario initial feed with an explicit affected context.
    fixture.componentInstance.store.open({
      scenarioId: ids.scenario,
      runId: ids.run,
      alertId: notificationIds.alert,
    });
    http
      .expectOne(`/api/scenarios/${ids.scenario}/runs?limit=100`)
      .flush([notificationRunFixture()]);
    http
      .expectOne(`/api/scenarios/${ids.scenario}/runs/${ids.run}`)
      .flush(notificationRunFixture());
    await vi.advanceTimersByTimeAsync(0);
    http
      .expectOne((r) => r.url.startsWith('/api/operational-notifications?'))
      .flush({ items: [alertFixture()], total: 1, offset: 0, limit: 25 });
    http
      .expectOne((r) => r.url.startsWith('/api/operational-notifications/activity?'))
      .flush({ items: [activityFixture()], total: 1, offset: 0, limit: 25 });
    http
      .expectOne(`/api/operational-notifications/${notificationIds.alert}`)
      .flush(detailFixture());
    fixture.detectChanges();
    return fixture;
  }
  it('shows real evidence and exact zone/run links, and requires an actor before audited acknowledgement', async () => {
    const fixture = await view(),
      component = fixture.componentInstance,
      host: HTMLElement = fixture.nativeElement;
    expect(host.textContent).toContain('Critică');
    expect(host.textContent).toContain('Prognoza cozilor');
    expect(host.textContent).toContain('observații unice');
    const link = host.querySelector<HTMLAnchorElement>('.context-links a')!;
    const query = new URL(link.href).searchParams;
    expect(query.get('scenarioId')).toBe(ids.scenario);
    expect(query.get('runId')).toBe(ids.run);
    expect(query.get('zoneId')).toBe(alertFixture().zoneId);
    const acknowledge = [...host.querySelectorAll('button')].find(
      (button) => button.textContent?.trim() === 'Confirmă alerta',
    )!;
    expect(acknowledge.disabled).toBe(true);
    component.actor = 'Supervisor';
    fixture.detectChanges();
    expect(acknowledge.disabled).toBe(false);
    acknowledge.click();
    const command = http.expectOne(
      `/api/operational-notifications/${notificationIds.alert}/actions`,
    );
    expect(command.request.body).toMatchObject({
      actor: 'Supervisor',
      action: 'acknowledge',
      revision: 1,
    });
    command.error(new ProgressEvent('error'));
    fixture.detectChanges();
    expect(host.textContent).toContain('Reîncearcă aceeași comandă');
    expect(acknowledge.disabled).toBe(true);
    fixture.destroy();
  });
  it('scales the severity gauge, remembers the operator and applies the quick filters', async () => {
    const fixture = await view(),
      component = fixture.componentInstance;
    // Peak 30 against warning 10 / critical 20: the scale is the peak itself.
    const gauge = component.gauge(alertFixture());
    expect(gauge.peak).toBe(100);
    expect(gauge.warning).toBeCloseTo(33.33, 1);
    expect(gauge.critical).toBeCloseTo(66.67, 1);
    component.rememberActor('Ana Pop ');
    expect(localStorage.setItem).toHaveBeenCalledWith('aerosynapse.notification-actor.v1', 'Ana Pop');
    component.setLevel('critical');
    component.toggleUnacknowledged();
    expect(component.store.filters()).toMatchObject({ level: 'critical', acknowledged: 'false', offset: 0 });
    http.match(() => true);
    fixture.destroy();
  });
  it('keeps threshold fields empty until the supervisor chooses them and blocks invalid publication', async () => {
    const fixture = await view(),
      component = fixture.componentInstance;
    expect(component.warningThreshold).toBeNull();
    expect(component.criticalThreshold).toBeNull();
    expect(component.canPublish()).toBe(false);
    component.publish();
    http.expectNone('/api/operational-notifications/queue-forecast');
    component.warningThreshold = 10;
    component.criticalThreshold = 10;
    expect(component.canPublish()).toBe(false);
    component.criticalThreshold = 20;
    expect(component.canPublish()).toBe(true);
    component.publish();
    const command = http.expectOne('/api/operational-notifications/queue-forecast');
    expect(command.request.body).toMatchObject({
      scenarioId: ids.scenario,
      runId: ids.run,
      warningThreshold: 10,
      criticalThreshold: 20,
      horizonMinutes: 15,
      useLatest: false,
    });
    command.error(new ProgressEvent('error'));
    fixture.destroy();
  });
  it('renders comments as text and preserves a draft through an automatic refresh of the same alert', async () => {
    const fixture = await view(),
      component = fixture.componentInstance;
    component.comment = 'Ciornă încă nesalvată';
    component.actor = 'Supervisor';
    fixture.detectChanges();
    await vi.advanceTimersByTimeAsync(5000);
    http
      .expectOne((r) => r.url.startsWith('/api/operational-notifications?'))
      .flush({ items: [alertFixture()], total: 1, offset: 0, limit: 25 });
    http
      .expectOne((r) => r.url.startsWith('/api/operational-notifications/activity?'))
      .flush({ items: [], total: 0, offset: 0, limit: 25 });
    const detail = detailFixture();
    detail.activity[0].kind = 'commented';
    detail.activity[0].text = '<img src=x onerror=alert(1)>';
    http.expectOne(`/api/operational-notifications/${notificationIds.alert}`).flush(detail);
    fixture.detectChanges();
    expect(component.comment).toBe('Ciornă încă nesalvată');
    expect(fixture.nativeElement.querySelector('.comment-text img')).toBeNull();
    expect(fixture.nativeElement.querySelector('.comment-text').textContent).toContain(
      '<img src=x',
    );
    fixture.destroy();
  });
  it('keeps the empty-feed message stable during automatic refreshes', async () => {
    const fixture = TestBed.createComponent(NotificationCenterComponent);
    fixture.detectChanges();
    http.expectOne('/api/scenarios').flush([]);
    await vi.advanceTimersByTimeAsync(0);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.alert-list .empty').textContent).toContain(
      'Se citesc alertele',
    );
    const answerEmpty = () => {
      http
        .expectOne((r) => r.url.startsWith('/api/operational-notifications?'))
        .flush({ items: [], total: 0, offset: 0, limit: 25 });
      http
        .expectOne((r) => r.url.startsWith('/api/operational-notifications/activity?'))
        .flush({ items: [], total: 0, offset: 0, limit: 25 });
    };
    answerEmpty();
    fixture.detectChanges();
    const message = fixture.nativeElement.querySelector('.alert-list .empty') as HTMLElement;
    const text = message.textContent;
    expect(text).toContain('Nicio alertă');
    for (let tick = 0; tick < 2; tick++) {
      await vi.advanceTimersByTimeAsync(5000);
      fixture.detectChanges();
      expect(message.textContent).toBe(text);
      expect(fixture.nativeElement.querySelector('.alert-list .empty')).toBe(message);
      expect(fixture.nativeElement.querySelector('.feed-status').textContent).not.toContain(
        'Se actualizează',
      );
      expect(fixture.nativeElement.querySelector('.alert-list').getAttribute('aria-busy')).toBe(
        'false',
      );
      answerEmpty();
      fixture.detectChanges();
      expect(message.textContent).toBe(text);
    }
    fixture.destroy();
  });
});
