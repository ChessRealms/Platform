import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component, createEnvironmentInjector, EnvironmentInjector } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { vi } from 'vitest';
import { AuthSession } from './auth-session';
import { authGuard } from './auth.guard';
import { RefreshJournal } from './refresh-journal';
import { SessionRecoveryPage } from './session-recovery-page';
import { createTestRefreshJournal } from './testing-refresh-journal';
import { installTestWebLocks } from './testing-web-locks';

@Component({ template: '' })
class EmptyPage {}

describe('AuthSession and protected routes', () => {
  const refreshUrl = '/api/identity/web/refresh';
  const gameId = '1ae1fd73-7d50-43e2-bc5c-f9ef232fcdad';
  const inviteUrl = `/games/${gameId}`;
  const inviteLoginUrl = `/login?gameId=${gameId}`;
  const refreshKey = 'chessrealms.web-session.refresh-in-progress';
  const response = {
    code: 'Success', accessToken: 'access-token', tokenType: 'Bearer', expiresIn: 600,
  };
  let http: HttpTestingController;
  let restoreLocks: () => void;
  let tabs: EnvironmentInjector[];
  let journal: ReturnType<typeof createTestRefreshJournal>;

  beforeEach(() => {
    localStorage.clear();
    restoreLocks = installTestWebLocks();
    tabs = [];
    journal = createTestRefreshJournal();
    TestBed.configureTestingModule({ providers: [
      provideHttpClient(),
      provideHttpClientTesting(),
      { provide: RefreshJournal, useValue: journal },
      provideRouter([
        { path: 'home', canActivate: [authGuard], component: EmptyPage },
        { path: 'games/:id', canActivate: [authGuard], component: EmptyPage },
        { path: 'login', component: EmptyPage },
        { path: 'session-unavailable', component: SessionRecoveryPage },
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

  it.each([true, false])('preserves an offline invite when session retry succeeds or requires login (%s)', async restoresSession => {
    const online = vi.spyOn(navigator, 'onLine', 'get').mockReturnValue(false);
    const harness = await RouterTestingHarness.create();
    const page = await harness.navigateByUrl(inviteUrl, SessionRecoveryPage);
    const router = TestBed.inject(Router);
    expect(router.url).toBe(`/session-unavailable?gameId=${gameId}`);
    http.expectNone(refreshUrl);

    online.mockReturnValue(true);
    const retry = page.retry();
    const refresh = await vi.waitFor(() => http.expectOne(refreshUrl));
    if (restoresSession) {
      refresh.flush(response);
    } else {
      refresh.flush({ code: 'InvalidRefreshToken' }, { status: 401, statusText: 'Unauthorized' });
    }
    await retry;
    expect(router.url).toBe(restoresSession ? inviteUrl : inviteLoginUrl);
  });

  it('keeps the invite in the sign-in link when a refresh response is lost', async () => {
    const harness = await RouterTestingHarness.create();
    const navigation = harness.navigateByUrl(inviteUrl, SessionRecoveryPage);
    (await vi.waitFor(() => http.expectOne(refreshUrl))).error(new ProgressEvent('error'));
    await navigation;
    const page = harness.routeNativeElement;
    expect(page?.querySelector('button')).toBeNull();
    expect(page?.querySelector('a')?.getAttribute('href')).toBe(inviteLoginUrl);
  });

  it('discards invalid game IDs supplied to session recovery', async () => {
    const harness = await RouterTestingHarness.create('/session-unavailable?gameId=invalid');
    expect(harness.routeNativeElement?.querySelector('a')?.getAttribute('href')).toBe('/login');
  });

  it('coalesces refresh within a tab and serializes refresh across tabs', async () => {
    const first = openTab();
    const second = openTab();
    const firstRefresh = first.ensureSession();
    expect(first.ensureSession()).toBe(firstRefresh);
    const secondRefresh = second.ensureSession();

    const request = await vi.waitFor(() => http.expectOne(refreshUrl));
    http.expectNone(refreshUrl);
    expect(await journal.isPending()).toBe(true);
    request.flush(response);
    expect(await firstRefresh).toBe('authenticated');

    const queued = await vi.waitFor(() => http.expectOne(refreshUrl));
    queued.flush({ ...response, accessToken: 'second-access-token' });
    expect(await secondRefresh).toBe('authenticated');
    expect(await journal.isPending()).toBe(false);
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
    expect(await journal.isPending()).toBe(true);
    http.expectNone(refreshUrl);
  });

  it('ignores a stale localStorage mirror after the journal was cleared', async () => {
    localStorage.setItem(refreshKey, 'indexeddb');
    const refresh = openTab().ensureSession();
    (await vi.waitFor(() => http.expectOne(refreshUrl))).flush(response);
    expect(await refresh).toBe('authenticated');
    expect(await journal.isPending()).toBe(false);
  });

  it.each(['journal', 'legacy'])('blocks an interrupted %s refresh', async source => {
    if (source === 'journal') {
      await journal.setPending(true);
    } else {
      localStorage.setItem(refreshKey, '1');
    }

    expect(await openTab().ensureSession()).toBe('sign-in-required');
    http.expectNone(refreshUrl);
  });

  it('requires working journal storage before sending a refresh', async () => {
    vi.spyOn(journal, 'setPending').mockRejectedValueOnce(new Error('Journal unavailable'));
    expect(await openTab().ensureSession()).toBe('unavailable');
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
