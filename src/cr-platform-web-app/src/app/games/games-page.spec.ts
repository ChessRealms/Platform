import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { of, Subject, throwError } from 'rxjs';
import { vi } from 'vitest';
import { AuthSession } from '../identity/auth-session';
import { gameIdPattern, GameListItem, GameListPage } from './game.models';
import { GamesApi } from './games-api';
import { GamesPage } from './games-page';

describe('My games', () => {
  const serverTime = '2026-01-01T00:00:00Z';
  const incoming: GameListItem = {
    id: 'a1', opponentId: 'p1', opponentName: 'Rook', color: 'Black', status: 'Pending', createdAt: null,
    opponentIsGuest: false,
    invitationExpiresAt: null,
    isYourTurn: false, winner: null, endReason: null, isLobby: false, timeControl: null,
  };
  const outgoing: GameListItem = { ...incoming, id: 'a2', opponentName: 'Knight', color: 'White' };
  const opponentId = '1ae1fd73-7d50-43e2-bc5c-f9ef232fcdad';
  const opponentUsername = 'BlackKnight';
  let fixture: ComponentFixture<GamesPage>;
  let element: HTMLElement;

  afterEach(() => {
    sessionStorage.clear();
    vi.useRealTimers();
  });

  function open(list: ReturnType<typeof vi.fn>, create = vi.fn(), createLobby = vi.fn()): void {
    TestBed.configureTestingModule({ providers: [
      provideRouter([]),
      { provide: AuthSession, useValue: { loginKey: () => 'current-login' } },
    ] })
      .overrideComponent(GamesPage, { set: { providers: [
        { provide: GamesApi, useValue: {
          list, create, createLobby, currentQuickPlayTicket: () => of(null),
          me: () => of({ userId: 'p2', username: 'WhiteKnight' }),
        } },
      ] } });
    render();
  }

  function render(): void {
    fixture = TestBed.createComponent(GamesPage);
    element = fixture.nativeElement;
    fixture.detectChanges();
  }

  function enterOpponent(): void {
    const input = element.querySelector<HTMLInputElement>('#opponent-username')!;
    input.value = ` ${opponentUsername} `;
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  function click(label: string): void {
    const button = [...element.querySelectorAll('button')].find(item => item.textContent!.trim() === label);
    expect(button).toBeDefined();
    button!.click();
    fixture.detectChanges();
  }

  it('shows both invitation directions and ignores responses from a previous filter', () => {
    const invitations = new Subject<GameListPage>();
    const active = { ...incoming, id: 'a3', opponentName: 'Bishop', status: 'Active', isYourTurn: true };
    const list = vi.fn().mockReturnValueOnce(invitations)
      .mockReturnValueOnce(of({ items: [active], nextCursor: null }));
    open(list);
    invitations.next({ items: [incoming, outgoing], nextCursor: null, serverTime });
    fixture.detectChanges();

    expect(element.textContent).toContain('Invitation received');
    expect(element.textContent).toContain('Waiting for acceptance');
    expect(element.querySelector('.game-row')?.getAttribute('href')).toBe('/games/a1');

    click('Active');
    invitations.next({ items: [incoming], nextCursor: null, serverTime });
    fixture.detectChanges();
    expect(list).toHaveBeenLastCalledWith('Active', null);
    expect(element.textContent).toContain('Your turn');
    expect(element.textContent).not.toContain('Invitation received');
    expect(element.querySelector('.game-row')?.getAttribute('href')).toBe('/games/a3');
  });

  it('keeps each page of invitation countdowns anchored to its own server response', () => {
    vi.useFakeTimers();
    vi.setSystemTime(Date.parse(serverTime) + 20 * 60_000);
    const invitationExpiresAt = '2026-01-01T00:00:10Z';
    const list = vi.fn()
      .mockReturnValueOnce(of({ items: [{ ...incoming, invitationExpiresAt }], nextCursor: incoming.id, serverTime }))
      .mockReturnValueOnce(of({
        items: [{ ...outgoing, invitationExpiresAt, isLobby: true }], nextCursor: null, serverTime: '2026-01-01T00:00:03Z',
      }));
    open(list);
    expect(element.querySelector('time')?.textContent).toBe('Expires in 0:10');

    vi.advanceTimersByTime(3000);
    click('Load more');
    expect([...element.querySelectorAll('time')].map(time => time.textContent)).toEqual(['Expires in 0:07', 'Expires in 0:07']);
  });

  it('reuses the private link request and its time control after a lost response and a reload', async () => {
    const list = vi.fn().mockReturnValue(of({ items: [], nextCursor: null }));
    const createLobby = vi.fn().mockReturnValueOnce(throwError(() => ({ status: 0 })))
      .mockReturnValueOnce(of({ id: opponentId }));
    open(list, undefined, createLobby);
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    await fixture.whenStable();
    const timeControl = element.querySelector<HTMLSelectElement>('#link-time-control')!;
    timeControl.value = 'Blitz5';
    timeControl.dispatchEvent(new Event('change'));
    click('Create private link');
    const { requestId } = createLobby.mock.calls[0][0];
    expect(requestId).toMatch(gameIdPattern);

    fixture.destroy();
    render();
    await fixture.whenStable();
    expect(element.querySelector<HTMLSelectElement>('#link-time-control')?.value).toBe('Blitz5');
    click('Retry creating link');
    expect(createLobby.mock.calls).toEqual([[{ requestId, timeControl: 'Blitz5' }], [{ requestId, timeControl: 'Blitz5' }]]);
    expect(navigate).toHaveBeenCalledWith(['/lobbies', opponentId]);
    expect(sessionStorage.length).toBe(0);
  });

  it('retains the current page and cursor when loading more fails', () => {
    const list = vi.fn().mockReturnValueOnce(of({ items: [incoming], nextCursor: incoming.id }))
      .mockReturnValueOnce(throwError(() => new Error('Unavailable')))
      .mockReturnValueOnce(of({ items: [outgoing], nextCursor: null }));
    open(list);

    click('Load more');
    expect(element.querySelector('[role="alert"]')?.textContent).toContain('Could not load your games');
    expect(element.querySelectorAll('.game-row')).toHaveLength(1);

    click('Load more');
    expect(list).toHaveBeenLastCalledWith('Pending', incoming.id);
    expect(element.querySelectorAll('.game-row')).toHaveLength(2);
    expect(element.querySelector('[role="alert"]')).toBeNull();
  });

  it('shows a resignation draw and its reason in the finished list', () => {
    const finished: GameListItem = { ...incoming, status: 'Finished', endReason: 'ResignationNoPossibleMate' };
    const list = vi.fn().mockReturnValueOnce(of({ items: [], nextCursor: null }))
      .mockReturnValueOnce(of({ items: [finished], nextCursor: null }));
    open(list);

    click('Finished');
    expect(element.querySelector('.game-status')?.textContent?.trim())
      .toBe('Draw · Insufficient mating material after resignation');
  });

  it('links to an already pending invitation and clears the rejected attempt', async () => {
    const list = vi.fn().mockReturnValue(of({ items: [], nextCursor: null }));
    const create = vi.fn().mockReturnValue(throwError(() => ({
      status: 409, error: { code: 'InvitationAlreadyPending', gameId: opponentId },
    })));
    open(list, create);
    await fixture.whenStable();
    enterOpponent();
    click('Send invitation');

    const link = [...element.querySelectorAll('a')].find(item => item.textContent === 'Open existing invitation');
    expect(link?.getAttribute('href')).toBe(`/games/${opponentId}`);
    expect(element.querySelector('[role="alert"]')?.textContent).toContain('already have a pending invitation');
    expect(sessionStorage.length).toBe(0);
  });

  it('reuses the creation request after a lost response and a reload', async () => {
    const list = vi.fn().mockReturnValue(of({ items: [], nextCursor: null }));
    const create = vi.fn().mockReturnValueOnce(throwError(() => ({ status: 0 })))
      .mockReturnValueOnce(of({ id: 'saved-game' }));
    open(list, create);
    await fixture.whenStable();
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    enterOpponent();
    click('Send invitation');
    const { requestId } = create.mock.calls[0][0];
    expect(requestId).toMatch(gameIdPattern);
    expect(element.textContent).toContain('Game creation hasn’t been confirmed');
    expect(navigate).not.toHaveBeenCalled();

    fixture.destroy();
    render();
    await fixture.whenStable();
    expect(element.querySelector<HTMLInputElement>('#opponent-username')?.value).toBe(opponentUsername);
    expect(element.querySelector<HTMLInputElement>('#opponent-username')?.readOnly).toBe(true);
    click('Retry creation');

    expect(create.mock.calls).toEqual([[{ opponentUsername, requestId }], [{ opponentUsername, requestId }]]);
    expect(navigate).toHaveBeenCalledWith(['/games', 'saved-game']);
    expect(sessionStorage.length).toBe(0);
  });

  it('retries a pending UUID invitation saved by an earlier version', () => {
    const requestId = 'a2f01c6d-b5f2-4dda-99f1-80c91c009c81';
    sessionStorage.setItem('chessrealms.pending-game-creation', JSON.stringify({
      login: 'current-login', opponentId, requestId,
    }));
    const list = vi.fn().mockReturnValue(of({ items: [], nextCursor: null }));
    const create = vi.fn().mockReturnValue(throwError(() => ({ status: 0 })));
    open(list, create);

    expect(element.querySelector<HTMLInputElement>('#opponent-id')?.value).toBe(opponentId);
    click('Retry creation');
    expect(create).toHaveBeenCalledExactlyOnceWith({ opponentId, requestId });

    click('Start a new invitation');
    expect(element.querySelector<HTMLInputElement>('#opponent-username')?.value).toBe('');
  });

  it('uses a fresh request ID when the player chooses to start a new invitation', async () => {
    const list = vi.fn().mockReturnValue(of({ items: [], nextCursor: null }));
    const create = vi.fn().mockReturnValue(throwError(() => ({ status: 0 })));
    open(list, create);
    await fixture.whenStable();
    enterOpponent();
    click('Send invitation');
    click('Start a new invitation');
    expect(create).toHaveBeenCalledTimes(1);
    click('Send invitation');
    expect(create).toHaveBeenCalledTimes(2);
    expect(create.mock.calls[1][0].opponentUsername).toBe(opponentUsername);
    expect(create.mock.calls[1][0].requestId).not.toBe(create.mock.calls[0][0].requestId);
  });
});
