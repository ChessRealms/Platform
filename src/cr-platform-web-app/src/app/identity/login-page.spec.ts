import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { NavigationEnd, provideRouter, Router } from '@angular/router';
import { firstValueFrom, filter } from 'rxjs';
import { AuthSession } from './auth-session';
import { ConfirmationSession } from './confirmation-session';
import { LoginPage } from './login-page';

@Component({ template: '' })
class EmptyPage {}

describe('LoginPage', () => {
  let http: HttpTestingController;
  let page: LoginPage;
  let router: Router;
  let session: AuthSession;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([
          { path: 'home', component: EmptyPage },
          { path: 'check-email', component: EmptyPage },
        ]),
      ],
    });
    page = TestBed.createComponent(LoginPage).componentInstance;
    http = TestBed.inject(HttpTestingController);
    router = TestBed.inject(Router);
    session = TestBed.inject(AuthSession);
    page.form.setValue({ email: ' player@example.test ', password: 'Password9!' });
  });

  afterEach(() => http.verify());

  it('opens the home page after a successful login', async () => {
    page.submit();
    const navigation = firstValueFrom(router.events.pipe(filter(event => event instanceof NavigationEnd)));
    const request = http.expectOne('/api/identity/login');
    expect(request.request.body).toEqual({ email: 'player@example.test', password: 'Password9!' });
    request.flush({
      code: 'Success', accessToken: 'access-secret', refreshToken: 'refresh-secret',
      tokenType: 'Bearer', expiresIn: 600, emailConfirmation: null, refreshTokenExpiresAt: null,
    });

    await navigation;
    expect(router.url).toBe('/home');
    expect(session.isAuthenticated()).toBe(true);
    expect(page.form.controls.password.value).toBe('');
  });

  it('shows invalid credentials and does not start a session', () => {
    page.submit();
    http.expectOne('/api/identity/login').flush({ code: 'InvalidCredentials' },
      { status: 401, statusText: 'Unauthorized' });

    expect(page.errorMessage()).toBe('Email or password is incorrect.');
    expect(session.isAuthenticated()).toBe(false);
  });

  it('opens the waiting page with a confirmation session after valid credentials', async () => {
    page.submit();
    const navigation = firstValueFrom(router.events.pipe(filter(event => event instanceof NavigationEnd)));
    http.expectOne('/api/identity/login').flush({
      code: 'EmailNotConfirmed',
      emailConfirmation: { sessionToken: 'confirmation-token' },
    }, { status: 403, statusText: 'Forbidden' });

    await navigation;
    expect(router.url).toBe('/check-email');
    expect(TestBed.inject(ConfirmationSession).take()?.sessionToken).toBe('confirmation-token');
    expect(session.isAuthenticated()).toBe(false);
    expect(page.form.controls.password.value).toBe('');
  });

  it('shows an explanation when confirmation metadata is unavailable', () => {
    page.submit();
    http.expectOne('/api/identity/login').flush({ code: 'EmailNotConfirmed' },
      { status: 403, statusText: 'Forbidden' });

    expect(page.errorMessage()).toBe('Confirm your email address before signing in.');
    expect(session.isAuthenticated()).toBe(false);
  });

  it('handles validation and network failures without starting a session', () => {
    page.submit();
    http.expectOne('/api/identity/login').flush({ errors: { Email: ['Required'] } },
      { status: 400, statusText: 'Bad Request' });
    expect(page.errorMessage()).toBe('Please check your email and password.');

    page.submit();
    http.expectOne('/api/identity/login').error(new ProgressEvent('error'));
    expect(page.errorMessage()).toContain('Cannot reach the server');
    expect(session.isAuthenticated()).toBe(false);
  });

  it('does not navigate when a success response is missing tokens', () => {
    page.submit();
    http.expectOne('/api/identity/login').flush({ code: 'Success', accessToken: null, refreshToken: null });

    expect(page.errorMessage()).toContain('could not be completed');
    expect(session.isAuthenticated()).toBe(false);
    expect(router.url).not.toBe('/home');
  });
});
