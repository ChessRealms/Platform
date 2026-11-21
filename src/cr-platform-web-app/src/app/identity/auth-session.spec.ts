import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component, createEnvironmentInjector, EnvironmentInjector } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { vi } from 'vitest';
import { AuthSession } from './auth-session';
import { authGuard } from './auth.guard';
import { installTestWebLocks } from './testing-web-locks';

@Component({ template: '' })
class EmptyPage {}

describe('AuthSession and protected routes', () => {
  const refreshUrl = '/api/identity/web/refresh';
  const response = {
    code: 'Success', accessToken: 'access-token', tokenType: 'Bearer', expiresIn: 600,
  };
  let http: HttpTestingController;
  let restoreLocks: () => void;
  let tabs: EnvironmentInjector[];

  beforeEach(() => {
    localStorage.clear();
    restoreLocks = installTestWebLocks();
    tabs = [];
    TestBed.configureTestingModule({ providers: [
      provideHttpClient(),
      provideHttpClientTesting(),
      provideRouter([
        { path: 'home', canActivate: [authGuard], component: EmptyPage },
        { path: 'login', component: EmptyPage },
        { path: 'session-unavailable', component: EmptyPage },
      ]),
    ] });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    tabs.forEach(tab => tab.destroy());
    restoreLocks();
    localStorage.clear();
    vi.restoreAllMocks();
    http.verify();
  });

  function openTab(): AuthSession {
    const tab = createEnvironmentInjector([AuthSession], TestBed.inject(EnvironmentInjector));
    tabs.push(tab);
    return tab.get(AuthSession);
  }

  it('holds protected navigation while restoring the session from a cookie', async () => {
    const router = TestBed.inject(Router);
    const navigation = router.navigateByUrl('/home');
    const refresh = await vi.waitFor(() => http.expectOne(refreshUrl));
    expect(router.url).toBe('/');
    expect(refresh.request.body).toBeNull();
    refresh.flush(response);
    await navigation;
    expect(router.url).toBe('/home');
    expect(JSON.stringify(localStorage)).not.toContain('access-token');
  });

  it('sends a guest to login after a rejected session', async () => {
    const router = TestBed.inject(Router);
    const navigation = router.navigateByUrl('/home');
    const refresh = await vi.waitFor(() => http.expectOne(refreshUrl));
    refresh.flush({ code: 'InvalidRefreshToken' }, { status: 401, statusText: 'Unauthorized' });
    await navigation;
    expect(router.url).toBe('/login');
  });

  it('coalesces refresh within a tab and serializes refresh across tabs', async () => {
    const first = openTab();
    const second = openTab();
    const firstRefresh = first.ensureSession();
    expect(first.ensureSession()).toBe(firstRefresh);
    const secondRefresh = second.ensureSession();

    const request = await vi.waitFor(() => http.expectOne(refreshUrl));
    http.expectNone(refreshUrl);
    request.flush(response);
    expect(await firstRefresh).toBe('authenticated');

    const queued = await vi.waitFor(() => http.expectOne(refreshUrl));
    queued.flush({ ...response, accessToken: 'second-access-token' });
    expect(await secondRefresh).toBe('authenticated');
  });

  it('refreshes an expired access token and uses the successor for logout', async () => {
    const session = openTab();
    const initialize = session.ensureSession();
    (await vi.waitFor(() => http.expectOne(refreshUrl))).flush(response);
    await initialize;

    vi.spyOn(Date, 'now').mockReturnValue(Date.now() + 601000);
    const logout = session.logout(false);
    (await vi.waitFor(() => http.expectOne(refreshUrl))).flush({ ...response, accessToken: 'successor' });
    const request = await vi.waitFor(() => http.expectOne('/api/identity/web/logout'));
    expect(request.request.headers.get('Authorization')).toBe('Bearer successor');
    request.flush(null, { status: 204, statusText: 'No Content' });
    expect(await logout).toBe(true);
    expect(session.state()).toBe('anonymous');
  });

  it('signs other tabs out and drops refresh work queued behind logout-all', async () => {
    const first = openTab();
    const second = openTab();
    const initialize = first.ensureSession();
    (await vi.waitFor(() => http.expectOne(refreshUrl))).flush(response);
    await initialize;
    const logout = first.logout(true);
    const request = await vi.waitFor(() => http.expectOne('/api/identity/web/logout-all'));
    const queuedRefresh = second.ensureSession();
    request.flush(null, { status: 204, statusText: 'No Content' });
    expect(await logout).toBe(true);
    expect(await queuedRefresh).toBe('anonymous');
    http.expectNone(refreshUrl);
  });

  it('does not replay refresh after an uncertain response', async () => {
    const first = openTab();
    const second = openTab();
    const attempted = first.ensureSession();
    const queued = second.ensureSession();
    const request = await vi.waitFor(() => http.expectOne(refreshUrl));
    request.error(new ProgressEvent('error'));
    expect(await attempted).toBe('sign-in-required');
    expect(await queued).toBe('sign-in-required');
    expect(await openTab().ensureSession(true)).toBe('sign-in-required');
    http.expectNone(refreshUrl);
  });

  it('lets the user retry when the browser was already offline', async () => {
    const session = openTab();
    const online = vi.spyOn(navigator, 'onLine', 'get').mockReturnValue(false);
    expect(await session.ensureSession()).toBe('unavailable');
    http.expectNone(refreshUrl);
    online.mockReturnValue(true);
    const retry = session.ensureSession(true);
    (await vi.waitFor(() => http.expectOne(refreshUrl))).flush(response);
    expect(await retry).toBe('authenticated');
  });
});
