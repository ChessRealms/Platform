import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { Subject } from 'rxjs';
import { vi } from 'vitest';
import { AuthSession } from '../identity/auth-session';
import { authGuard } from '../identity/auth.guard';
import { LoginPage } from '../identity/login-page';
import { RefreshJournal } from '../identity/refresh-journal';
import { SessionRecoveryPage } from '../identity/session-recovery-page';
import { createTestRefreshJournal } from '../identity/testing-refresh-journal';
import { installTestWebLocks } from '../identity/testing-web-locks';
import { GameLiveUpdates } from './game-live-updates';
import { GameSessionPage } from './game-session-page';
import { GameBoard } from './game.models';

const id = '1ae1fd73-7d50-43e2-bc5c-f9ef232fcdad';
const position: GameBoard = {
  game: { id, whitePlayerId: 'white', blackPlayerId: 'black', status: 'Active', revision: 1,
    fen: '7k/P7/8/8/8/8/8/7K w - - 0 1', sideToMove: 'White', winner: null, endReason: null, drawOfferedBy: null },
  moves: [], legalMoves: ['a7a8q'], drawClaims: [],
};

describe('games across browser logins', () => {
  function setup() {
    const loginKey = signal<string | null>('white');
    const state = signal('authenticated');
    const updates = new Subject<number | null>();
    TestBed.configureTestingModule({ providers: [
      provideHttpClient(), provideHttpClientTesting(),
      provideRouter([{ path: 'games', component: GameSessionPage }, { path: 'games/:id', component: GameSessionPage }]),
      { provide: AuthSession, useValue: { loginKey, state, getAccessToken: async (key: string) => key === loginKey() ? key : null } },
      { provide: GameLiveUpdates, useValue: { watch: () => updates } },
    ] });
    const http = TestBed.inject(HttpTestingController);
    const request = (url: string) => vi.waitFor(() => http.expectOne(url));
    return { loginKey, state, http, request, updates };
  }

  it('keeps the selection during refresh and reads a full board for a new player at the same revision', async () => {
    const { loginKey, state, http, request, updates } = setup();
    const boardUrl = `/api/games/${id}/board`;
    const harness = await RouterTestingHarness.create('/games/' + id);
    (await request('/api/identity/me')).flush({ userId: 'white' });
    (await request(boardUrl)).flush(position);
    harness.detectChanges();
    const page = harness.routeNativeElement!;
    const pawn = page.querySelector<HTMLButtonElement>('[data-square="a7"]')!;
    pawn.click();
    state.set('checking');
    harness.detectChanges();
    expect(pawn.getAttribute('aria-pressed')).toBe('true');
    state.set('authenticated');
    updates.next(2);
    const lateBoard = await request(boardUrl + '?afterRevision=1');

    loginKey.set('black');
    harness.detectChanges();
    expect(updates.observed).toBe(false);
    expect(lateBoard.cancelled).toBe(true);
    expect(page.querySelector('.board')).toBeNull();
    const nextBoard = await request(boardUrl);
    expect(nextBoard.request.headers.get('Authorization')).toBe('Bearer black');
    nextBoard.flush({ ...position, legalMoves: [] });
    (await request('/api/identity/me')).flush({ userId: 'black' });
    harness.detectChanges();
    expect(page.textContent).toContain('You play Black');
    expect(page.querySelector('[aria-pressed="true"]')).toBeNull();

    loginKey.set('outsider');
    harness.detectChanges();
    (await request('/api/identity/me')).flush({ userId: 'outsider' });
    (await request(boardUrl)).flush(null, { status: 404, statusText: 'Not found' });
    harness.detectChanges();
    expect(page.textContent).toContain('This game is unavailable');
    expect(page.querySelector('.board')).toBeNull();
    http.verify();
  });

  it('discards the old list, pending creation and form data when the login changes', async () => {
    const { loginKey, http, request } = setup();
    const harness = await RouterTestingHarness.create('/games');
    (await request('/api/identity/me')).flush({ userId: 'white' });
    const lateList = await request('/api/games?status=Pending');
    harness.detectChanges();
    const page = harness.routeNativeElement!;
    const input = page.querySelector<HTMLInputElement>('#opponent-id')!;
    input.value = id;
    input.dispatchEvent(new Event('input'));
    page.querySelector('form')!.dispatchEvent(new Event('submit'));
    const lateCreation = await request('/api/games');
    loginKey.set('black');
    // The response can arrive before Angular has destroyed the previous view.
    lateCreation.flush(position.game);
    harness.detectChanges();
    expect(lateList.cancelled).toBe(true);
    (await request('/api/identity/me')).flush({ userId: 'black' });
    (await request('/api/games?status=Pending')).flush({ items: [], nextCursor: null });
    harness.detectChanges();
    expect(page.querySelector<HTMLInputElement>('#opponent-id')!.value).toBe('');
    expect(page.querySelector<HTMLInputElement>('#player-id')!.value).toBe('black');
    http.verify();
  });
});

describe('session recovery on an open game board', () => {
  const gameUrl = `/games/${id}`;
  const boardUrl = `/api${gameUrl}/board`;
  const moveUrl = `/api${gameUrl}/moves`;
  const refreshUrl = '/api/identity/web/refresh';
  const loginUrl = `/login?gameId=${id}`;
  const recoveryUrl = `/session-unavailable?gameId=${id}`;
  const sessionResponse = { code: 'Success', tokenType: 'Bearer', accessToken: 'game-access', expiresIn: 600 };
  const moved: GameBoard = {
    game: { ...position.game, fen: 'Q6k/8/8/8/8/8/8/7K b - - 0 1', revision: 2, sideToMove: 'Black' },
    moves: ['a7a8q'], legalMoves: [], drawClaims: [],
  };
  let http: HttpTestingController;
  let router: Router;
  let harness: RouterTestingHarness;
  let updates: Subject<number | null>;
  let restoreLocks: () => void;

  beforeEach(async () => {
    localStorage.clear();
    restoreLocks = installTestWebLocks();
    updates = new Subject<number | null>();
    TestBed.configureTestingModule({ providers: [
      provideHttpClient(), provideHttpClientTesting(),
      provideRouter([
        { path: 'games/:id', canActivate: [authGuard], component: GameSessionPage },
        { path: 'session-unavailable', component: SessionRecoveryPage },
        { path: 'login', component: LoginPage },
      ]),
      { provide: RefreshJournal, useValue: createTestRefreshJournal() },
      { provide: GameLiveUpdates, useValue: { watch: () => updates } },
    ] });
    http = TestBed.inject(HttpTestingController);
    router = TestBed.inject(Router);
    harness = await RouterTestingHarness.create();
    const navigation = harness.navigateByUrl(gameUrl, GameSessionPage);
    (await request(refreshUrl)).flush(sessionResponse);
    await navigation;
    await showBoard(position);
  });

  afterEach(() => {
    restoreLocks();
    vi.restoreAllMocks();
    localStorage.clear();
    http.verify();
  });

  function request(url: string) {
    return vi.waitFor(() => http.expectOne(url));
  }

  async function showBoard(board: GameBoard): Promise<void> {
    (await request('/api/identity/me')).flush({ userId: 'white' });
    (await request(boardUrl)).flush(board);
    harness.detectChanges();
  }

  function page(): HTMLElement {
    return harness.routeNativeElement!;
  }

  function expireAccessToken(): void {
    vi.spyOn(Date, 'now').mockReturnValue(Date.now() + 601000);
  }

  it('reads the latest board when the connection returns without replaying a move', async () => {
    page().querySelector<HTMLButtonElement>('[data-square="a7"]')!.click();
    page().querySelector<HTMLButtonElement>('[data-square="a8"]')!.click();
    const move = await request(moveUrl);
    expect(move.request.method).toBe('POST');
    expect(move.request.body).toEqual({ expectedRevision: 1, move: 'a7a8q' });
    move.error(new ProgressEvent('error'));
    const currentBoardUrl = boardUrl + '?afterRevision=1';
    (await request(currentBoardUrl)).error(new ProgressEvent('error'));
    harness.detectChanges();

    expect(page().textContent).toContain('Connection interrupted');
    expect(page().querySelector('.board')).not.toBeNull();
    expect(page().querySelector('a[href^="/login"]')).toBeNull();

    window.dispatchEvent(new Event('online'));
    (await request(currentBoardUrl)).flush(moved);
    harness.detectChanges();
    expect(page().textContent).not.toContain('Connection interrupted');
    expect(page().textContent).toContain('a7a8q');
    http.expectNone(moveUrl);
    http.expectNone(refreshUrl);
  });

  it('returns from session recovery to the same game after the access token expired offline', async () => {
    const online = vi.spyOn(navigator, 'onLine', 'get').mockReturnValue(false);
    expireAccessToken();
    updates.next(null);
    await vi.waitFor(() => {
      harness.detectChanges();
      expect(page().querySelector(`a[href="${recoveryUrl}"]`)).not.toBeNull();
    });
    expect(page().querySelector('.board')).not.toBeNull();
    const resign = [...page().querySelectorAll('button')].find(button => button.textContent === 'Resign');
    expect(resign?.disabled).toBe(true);
    expect(page().querySelector('a[href^="/login"]')).toBeNull();
    http.expectNone(refreshUrl);

    const recovery = await harness.navigateByUrl(recoveryUrl, SessionRecoveryPage);
    online.mockReturnValue(true);
    const retry = recovery.retry();
    (await request(refreshUrl)).flush(sessionResponse);
    await retry;
    expect(router.url).toBe(gameUrl);
    await showBoard(moved);
    expect(page().textContent).toContain('a7a8q');
  });

  it('requires a new sign-in after a lost refresh response and reopens the same game', async () => {
    expireAccessToken();
    updates.next(null);
    (await request(refreshUrl)).error(new ProgressEvent('error'));
    await vi.waitFor(() => {
      harness.detectChanges();
      expect(page().textContent).toContain('Sign in again to continue');
    });
    expect(page().querySelector('.board')).toBeNull();
    expect(page().querySelector(`a[href="${loginUrl}"]`)).not.toBeNull();
    expect(page().querySelector('a[href^="/session-unavailable"]')).toBeNull();
    http.expectNone(refreshUrl);

    const login = await harness.navigateByUrl(loginUrl, LoginPage);
    login.form.setValue({ email: 'player@example.test', password: 'Synthetic-password9!' });
    const signIn = login.submit();
    (await request('/api/identity/web/login')).flush(sessionResponse);
    await signIn;
    await vi.waitFor(() => expect(router.url).toBe(gameUrl));
    await showBoard(moved);
    expect(page().textContent).toContain('a7a8q');
    http.expectNone(refreshUrl);
  });
});
