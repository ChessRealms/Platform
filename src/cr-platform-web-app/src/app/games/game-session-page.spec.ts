import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { Subject } from 'rxjs';
import { vi } from 'vitest';
import { AuthSession } from '../identity/auth-session';
import { GameLiveUpdates } from './game-live-updates';
import { GameSessionPage } from './game-session-page';
import { GameBoard } from './game.models';

describe('games across browser logins', () => {
  const id = '1ae1fd73-7d50-43e2-bc5c-f9ef232fcdad';
  const position: GameBoard = {
    game: { id, whitePlayerId: 'white', blackPlayerId: 'black', status: 'Active', revision: 1,
      fen: '7k/P7/8/8/8/8/8/7K w - - 0 1', sideToMove: 'White', winner: null, endReason: null, drawOfferedBy: null },
    moves: [], legalMoves: ['a7a8q'], drawClaims: [],
  };

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
