import { Location } from '@angular/common';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, provideRouter } from '@angular/router';
import { ForgotPasswordPage } from './forgot-password-page';
import { ResetPasswordPage } from './reset-password-page';

describe('Password recovery', () => {
  const userId = 'ed8d7f8f-daa5-4b69-b486-7dbed60a556a';
  const token = 'test-reset-token';
  const resetUrl = '/api/identity/password-reset/complete';
  let http: HttpTestingController;

  function configure(fragment: string | null = null): void {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { snapshot: { fragment } } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
  }

  afterEach(() => http.verify());

  it('requests a link from the email form and shows the generic accepted state', () => {
    configure();
    const fixture = TestBed.createComponent(ForgotPasswordPage);
    fixture.detectChanges();
    const page = fixture.componentInstance;
    const host: HTMLElement = fixture.nativeElement;
    const email = host.querySelector<HTMLInputElement>('#reset-email')!;
    email.value = 'player@example.test';
    email.dispatchEvent(new Event('input', { bubbles: true }));
    host.querySelector('form')!.dispatchEvent(new Event('submit', { bubbles: true }));
    page.submit();
    const request = http.expectOne({ method: 'POST', url: '/api/identity/password-reset/request' });
    expect(request.request.body).toEqual({ email: 'player@example.test' });
    request.flush(null, { status: 202, statusText: 'Accepted' });
    expect(page.sent()).toBe(true);
  });

  it('strips the fragment, waits for the form, and allows a retry after a password policy error', () => {
    const fragment = `userId=${userId}&token=${token}`;
    configure(fragment);
    const location = TestBed.inject(Location);
    location.go(`/reset-password#${fragment}`);
    const page = TestBed.createComponent(ResetPasswordPage).componentInstance;
    expect(location.path(true)).toBe('/reset-password');
    http.expectNone(resetUrl);

    page.form.setValue({ password: 'weak', confirmation: 'different' });
    page.submit();
    expect(page.errors()).toEqual(['The passwords do not match.']);
    http.expectNone(resetUrl);

    page.form.controls.confirmation.setValue('weak');
    page.submit();
    const invalidPassword = http.expectOne({ method: 'POST', url: resetUrl });
    expect(invalidPassword.request.body).toEqual({ userId, token, newPassword: 'weak' });
    invalidPassword.flush({ code: 'InvalidPassword', errors: [{ description: 'Password policy error.' }] },
      { status: 400, statusText: 'Bad Request' });
    expect(page.errors()).toEqual(['Password policy error.']);

    page.form.setValue({ password: 'New-password9!', confirmation: 'New-password9!' });
    page.submit();
    http.expectOne(resetUrl).flush(null, { status: 204, statusText: 'No Content' });
    expect(page.state()).toBe('complete');
    expect(page.form.controls.password.value).toBe('');
  });

  it('clears the form and stops using a rejected token', () => {
    configure(`userId=${userId}&token=${token}`);
    const page = TestBed.createComponent(ResetPasswordPage).componentInstance;
    page.form.setValue({ password: 'New-password9!', confirmation: 'New-password9!' });
    page.submit();
    http.expectOne(resetUrl).flush({ code: 'InvalidResetToken' }, { status: 400, statusText: 'Bad Request' });
    expect(page.state()).toBe('invalid');
    expect(page.form.controls.password.value).toBe('');
    page.submit();
    http.expectNone(resetUrl);
  });
});
