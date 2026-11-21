import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting, TestRequest } from '@angular/common/http/testing';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { NavigationEnd, provideRouter, Router } from '@angular/router';
import { firstValueFrom, filter } from 'rxjs';
import { AuthSession } from './auth-session';
import { ConfirmationSession } from './confirmation-session';
import { LoginPage } from './login-page';
import { installTestWebLocks } from './testing-web-locks';

@Component({ template: '' })
class EmptyPage {}

describe('LoginPage', () => {
  let http: HttpTestingController;
  let page: LoginPage;
  let router: Router;
  let session: AuthSession;
  let restoreLocks: () => void;

  beforeEach(() => {
    localStorage.clear();
    restoreLocks = installTestWebLocks();
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

  afterEach(() => {
    restoreLocks();
    localStorage.clear();
    http.verify();
  });

  async function submit(): Promise<{ request: TestRequest; completed: Promise<void> }> {
    const completed = page.submit();
    await Promise.resolve();
    return { request: http.expectOne('/api/identity/web/login'), completed };
  }

  it('opens the home page after a successful login', async () => {
    const navigation = firstValueFrom(router.events.pipe(filter(event => event instanceof NavigationEnd)));
    const { request, completed } = await submit();
    expect(request.request.body).toEqual({ email: 'player@example.test', password: 'Password9!' });
    request.flush({
      code: 'Success', accessToken: 'access-secret',
      tokenType: 'Bearer', expiresIn: 600, emailConfirmation: null, refreshTokenExpiresAt: null,
    });

    await Promise.all([navigation, completed]);
    expect(router.url).toBe('/home');
    expect(session.hasSession()).toBe(true);
    expect(page.form.controls.password.value).toBe('');
  });

  it('shows invalid credentials and does not start a session', async () => {
    const { request, completed } = await submit();
    request.flush({ code: 'InvalidCredentials' },
      { status: 401, statusText: 'Unauthorized' });
    await completed;

    expect(page.errorMessage()).toBe('Email or password is incorrect.');
    expect(session.hasSession()).toBe(false);
  });

  it('opens the waiting page with a confirmation session after valid credentials', async () => {
    const navigation = firstValueFrom(router.events.pipe(filter(event => event instanceof NavigationEnd)));
    const { request, completed } = await submit();
    request.flush({
      code: 'EmailNotConfirmed',
      emailConfirmation: { sessionToken: 'confirmation-token' },
    }, { status: 403, statusText: 'Forbidden' });

    await Promise.all([navigation, completed]);
    expect(router.url).toBe('/check-email');
    expect(TestBed.inject(ConfirmationSession).take()?.sessionToken).toBe('confirmation-token');
    expect(session.hasSession()).toBe(false);
    expect(page.form.controls.password.value).toBe('');
  });

  it('shows an explanation when confirmation metadata is unavailable', async () => {
    const { request, completed } = await submit();
    request.flush({ code: 'EmailNotConfirmed' },
      { status: 403, statusText: 'Forbidden' });
    await completed;

    expect(page.errorMessage()).toBe('Confirm your email address before signing in.');
    expect(session.hasSession()).toBe(false);
  });

  it('handles validation and network failures without starting a session', async () => {
    const validation = await submit();
    validation.request.flush({ errors: { Email: ['Required'] } },
      { status: 400, statusText: 'Bad Request' });
    await validation.completed;
    expect(page.errorMessage()).toBe('Please check your email and password.');

    const network = await submit();
    network.request.error(new ProgressEvent('error'));
    await network.completed;
    expect(page.errorMessage()).toContain('Cannot reach the server');
    expect(session.hasSession()).toBe(false);
  });

  it('does not navigate when a success response is missing the access token', async () => {
    const { request, completed } = await submit();
    request.flush({ code: 'Success', accessToken: null });
    await completed;

    expect(page.errorMessage()).toContain('could not be completed');
    expect(session.hasSession()).toBe(false);
    expect(router.url).not.toBe('/home');
  });
});
