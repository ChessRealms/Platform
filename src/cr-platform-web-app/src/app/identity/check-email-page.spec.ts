import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { vi } from 'vitest';
import { CheckEmailPage } from './check-email-page';
import { ConfirmationSession } from './confirmation-session';

describe('CheckEmailPage', () => {
  const statusUrl = '/api/identity/email-confirmation';
  const resendUrl = statusUrl + '/resend';
  const start = new Date('2026-11-21T12:00:00Z').getTime();
  let http: HttpTestingController;
  let fixture: ComponentFixture<CheckEmailPage>;

  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(start);
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    fixture?.destroy();
    http.verify();
    vi.useRealTimers();
  });

  function metadata(deliveryStatus = 'Queued', delay = 0) {
    return {
      sessionToken: null,
      expiresAt: new Date(start + 60000).toISOString(),
      deliveryStatus,
      resendAvailableAt: new Date(Date.now() + delay).toISOString(),
      failureCode: deliveryStatus === 'Failed' ? 'ProviderFailure' : null,
    };
  }

  function open(delay = 0): CheckEmailPage {
    TestBed.inject(ConfirmationSession).start({ ...metadata('Queued', delay), sessionToken: 'opaque-token' });
    fixture = TestBed.createComponent(CheckEmailPage);
    fixture.detectChanges();
    return fixture.componentInstance;
  }

  it('polls through queued and sent, then stops when confirmed in another tab', () => {
    const page = open();
    vi.advanceTimersByTime(0);
    const first = http.expectOne(statusUrl);
    expect(first.request.headers.get('Authorization')).toBe('Bearer opaque-token');

    vi.advanceTimersByTime(3000);
    http.expectNone(statusUrl);
    first.flush({ code: 'EmailNotConfirmed', emailConfirmation: metadata() });
    vi.advanceTimersByTime(3000);
    http.expectOne(statusUrl).flush({ code: 'EmailNotConfirmed', emailConfirmation: metadata('Sent') });

    vi.advanceTimersByTime(3000);
    http.expectNone(statusUrl);
    vi.advanceTimersByTime(9000);
    http.expectOne(statusUrl).flush({ code: 'EmailConfirmed', emailConfirmation: metadata('Sent') });
    vi.advanceTimersByTime(30000);
    http.expectNone(statusUrl);
    expect(page.state()).toBe('confirmed');
  });

  it('counts down resend, retains the token after 429, and shows the new delivery state', () => {
    const page = open(2000);
    page.resend();
    http.expectNone(resendUrl);
    vi.advanceTimersByTime(0);
    http.expectOne(statusUrl).flush({ code: 'EmailNotConfirmed', emailConfirmation: metadata('Failed', 2000) });
    expect(page.deliveryMessage()).toContain('couldn’t send');

    vi.advanceTimersByTime(2000);
    page.resend();
    const limited = http.expectOne(resendUrl);
    expect(limited.request.headers.get('Authorization')).toBe('Bearer opaque-token');
    limited.flush({ code: 'RateLimited', emailConfirmation: metadata('Failed', 5000) },
      { status: 429, statusText: 'Too Many Requests' });
    expect(page.resendSeconds()).toBe(5);
    expect(page.canResend()).toBe(false);

    vi.advanceTimersByTime(5000);
    page.resend();
    const accepted = http.expectOne(resendUrl);
    expect(accepted.request.headers.get('Authorization')).toBe('Bearer opaque-token');
    accepted.flush({ code: 'Queued', emailConfirmation: metadata('Queued', 2000) });
    expect(page.deliveryMessage()).toContain('queued');
  });

  it('offers recovery when the server rejects the confirmation session', () => {
    const page = open();
    vi.advanceTimersByTime(0);
    http.expectOne(statusUrl).flush({ code: 'InvalidSession' }, { status: 401, statusText: 'Unauthorized' });
    vi.advanceTimersByTime(30000);
    http.expectNone(statusUrl);
    expect(page.state()).toBe('expired');
  });

  it('offers recovery on direct navigation without a session', () => {
    fixture = TestBed.createComponent(CheckEmailPage);
    fixture.detectChanges();
    vi.advanceTimersByTime(3000);
    http.expectNone(statusUrl);
    expect(fixture.nativeElement.textContent).toContain('Sign in again');
  });
});
