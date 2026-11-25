import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter, Router } from '@angular/router';
import { BehaviorSubject, NEVER, Observable, of, Subject, throwError } from 'rxjs';
import { vi } from 'vitest';
import { AuthSession } from '../identity/auth-session';
import { GameBoardSync } from './game-board-sync';
import { GamePage } from './game-page';
import { GameLiveUpdates } from './game-live-updates';
import { Game, GameBoard } from './game.models';
import { GamesApi } from './games-api';

describe('GamePage', () => {
  const id = '1ae1fd73-7d50-43e2-bc5c-f9ef232fcdad';
  const game: Game = {
    id, whitePlayerId: 'white', blackPlayerId: 'black', status: 'Active', revision: 22,
    whiteGuestName: null, blackGuestName: null,
    fen: '7k/P7/8/8/8/8/8/7K w - - 0 12', sideToMove: 'White', winner: null, endReason: null, drawOfferedBy: null,
    invitationExpiresAt: null,
    rematchExpiresAt: null, rematchOfferedBy: null, rematchGameId: null,
    clock: null,
    serverTime: '2026-01-01T00:00:00Z',
  };
  const initial: GameBoard = { game, moves: [], legalMoves: ['a7a8q', 'a7a8r', 'a7a8b', 'a7a8n'], drawClaims: [] };

  afterEach(() => vi.useRealTimers());

  function open(board: ReturnType<typeof vi.fn>, move = vi.fn().mockReturnValue(of(game)),
    updates: Observable<number | null> = NEVER, params = of(convertToParamMap({ id })), updateInvitation = vi.fn()) {
    const watch = vi.fn(() => updates);
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: AuthSession, useValue: { state: () => 'authenticated' } },
        { provide: GameLiveUpdates, useValue: { watch } },
        { provide: ActivatedRoute, useValue: { paramMap: params } },
      ],
    }).overrideComponent(GamePage, { set: { providers: [
      GameBoardSync,
      { provide: GamesApi, useValue: { me: () => of({ userId: 'white' }), board, move, updateInvitation } },
    ] } });
    const fixture = TestBed.createComponent(GamePage);
    return { fixture, page: fixture.componentInstance, sync: fixture.debugElement.injector.get(GameBoardSync), watch };
  }

  it('uses server legal moves and requires an explicit promotion choice', () => {
    const move = vi.fn().mockReturnValue(of(game));
    const { page } = open(vi.fn().mockReturnValue(of(initial)), move);
    page.select('a7');
    expect([...page.targets()]).toEqual(['a8']);
    page.select('a8');
    expect(page.promotion()).toEqual(initial.legalMoves);
    expect(move).not.toHaveBeenCalled();
    page.play('a7a8n');
    expect(move).toHaveBeenCalledWith(game, 'a7a8n');
  });

  it('reads the current board after a revision conflict without resending the move', () => {
    const latest = { ...initial, game: { ...game, revision: 23 }, legalMoves: [] };
    const board = vi.fn().mockReturnValueOnce(of(initial)).mockReturnValue(of(latest));
    const move = vi.fn().mockReturnValue(throwError(() => new HttpErrorResponse({ status: 409 })));
    const { page, sync } = open(board, move);
    page.select('a7');
    page.select('a8');
    page.play('a7a8q');
    expect(move).toHaveBeenCalledTimes(1);
    expect(board).toHaveBeenLastCalledWith(id, 22);
    expect(page.game()?.revision).toBe(23);
    expect(page.promotion()).toEqual([]);
    expect(sync.commandError()).toContain('The game changed');
  });

  it('opens a finished game once and explains a resignation draw', () => {
    vi.useFakeTimers();
    const finished: Game = {
      ...game, status: 'Finished', sideToMove: null, endReason: 'ResignationNoPossibleMate',
    };
    const board = vi.fn().mockReturnValue(of({ ...initial, game: finished, legalMoves: [] }));
    const { page, watch } = open(board);

    expect(page.status()).toBe('Draw · Insufficient mating material after resignation');
    expect(watch).not.toHaveBeenCalled();
    window.dispatchEvent(new Event('online'));
    vi.advanceTimersByTime(45000);
    expect(board).toHaveBeenCalledTimes(1);
  });

  it.each([-20, 20])('hides rematch actions at the server deadline with a %i-minute browser clock offset', offset => {
    vi.useFakeTimers();
    vi.setSystemTime(Date.parse(game.serverTime) + offset * 60_000);
    const updates = new Subject<number | null>();
    const finished: Game = {
      ...game, status: 'Finished', sideToMove: null, rematchOfferedBy: 'Black',
      rematchExpiresAt: '2026-01-01T00:00:05Z',
    };
    const board = vi.fn().mockReturnValueOnce(of({ ...initial, game: finished, legalMoves: [] }))
      .mockReturnValue(of(null));
    const { fixture, sync } = open(board, undefined, updates);
    fixture.detectChanges();
    const acceptButton = () => [...(fixture.nativeElement as HTMLElement).querySelectorAll('button')]
      .find(button => button.textContent === 'Accept rematch');

    expect(acceptButton()?.disabled).toBe(false);
    expect(fixture.nativeElement.querySelector('time')?.textContent).toBe('Expires in 0:05');
    expect(updates.observed).toBe(true);
    vi.advanceTimersByTime(5000);
    fixture.detectChanges();
    expect(acceptButton()).toBeUndefined();
    expect(sync.submit({ kind: 'rematch', action: 'accept' })).toBe(false);

    vi.advanceTimersByTime(15000);
    expect(board).toHaveBeenLastCalledWith(id, finished.revision);
    expect(updates.observed).toBe(false);
    const reads = board.mock.calls.length;
    vi.advanceTimersByTime(45000);
    expect(board).toHaveBeenCalledTimes(reads);
  });

  it('discovers a late rematch acceptance through the final read when SignalR missed it', () => {
    vi.useFakeTimers();
    vi.setSystemTime(Date.parse(game.serverTime) + 20 * 60_000);
    const updates = new Subject<number | null>();
    const finished: Game = {
      ...game, status: 'Finished', sideToMove: null, rematchOfferedBy: 'Black',
      rematchExpiresAt: '2026-01-01T00:00:05Z',
    };
    const accepted: Game = { ...finished, revision: 23, rematchGameId: 'rematch' };
    const board = vi.fn()
      .mockReturnValueOnce(of({ ...initial, game: finished, legalMoves: [] }))
      .mockReturnValueOnce(of(null))
      .mockReturnValueOnce(of({ ...initial, game: accepted, legalMoves: [] }));
    const { fixture, sync } = open(board, undefined, updates);
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);

    vi.advanceTimersByTime(5000);
    expect(sync.canRematch()).toBe(false);
    expect(updates.observed).toBe(true);
    vi.advanceTimersByTime(15000);
    expect(sync.startedRematch()).toBe('rematch');
    expect(updates.observed).toBe(false);
    fixture.detectChanges();
    expect(navigate).toHaveBeenCalledExactlyOnceWith(['/games', 'rematch']);
  });

  it('requests the authoritative result when the running clock expires', () => {
    vi.useFakeTimers();
    const serverNow = Date.parse('2026-01-01T00:00:00Z');
    const clock = {
      timeControl: 'Blitz5' as const, whiteMilliseconds: 300000, blackMilliseconds: 300000,
      turnDeadline: new Date(serverNow + 1000).toISOString(), serverTime: new Date(serverNow).toISOString(),
    };
    const board = vi.fn().mockReturnValueOnce(of({ ...initial, game: { ...game, clock } }))
      .mockReturnValueOnce(of({
        ...initial, legalMoves: [],
        game: {
          ...game, revision: 23, status: 'Finished', winner: 'Black', endReason: 'Timeout', sideToMove: null,
          clock: { ...clock, whiteMilliseconds: 0, turnDeadline: null },
        },
      }));
    const { page } = open(board);

    vi.advanceTimersByTime(1000);
    expect(page.status()).toBe('Your turn');
    vi.advanceTimersByTime(50);
    expect(board).toHaveBeenLastCalledWith(id, 22);
    expect(page.status()).toBe('Black wins · Time expired');
  });

  it('reloads after an invitation conflict and stops watching when the invitation is closed', () => {
    vi.useFakeTimers();
    const updates = new Subject<number | null>();
    const pending: Game = { ...game, status: 'Pending', revision: 0, sideToMove: null };
    const declined: Game = { ...pending, status: 'Declined', revision: 1 };
    const board = vi.fn().mockReturnValueOnce(of({ ...initial, game: pending, legalMoves: [] }))
      .mockReturnValueOnce(of({ ...initial, game: declined, legalMoves: [] }));
    const updateInvitation = vi.fn().mockReturnValue(throwError(() => new HttpErrorResponse({ status: 409 })));
    const { page, sync } = open(board, undefined, updates, undefined, updateInvitation);

    expect(updates.observed).toBe(true);
    expect(sync.submit({ kind: 'invitation', action: 'cancel' })).toBe(true);
    expect(updateInvitation).toHaveBeenCalledExactlyOnceWith(pending, 'cancel');
    expect(board).toHaveBeenLastCalledWith(id, 0);
    expect(page.status()).toBe('Invitation declined');
    expect(sync.commandError()).toBe('The invitation changed. Check its current status.');
    expect(sync.canAct()).toBe(false);
    expect(updates.observed).toBe(false);

    window.dispatchEvent(new Event('online'));
    vi.advanceTimersByTime(45000);
    expect(board).toHaveBeenCalledTimes(2);
  });

  it('reads the board at the invitation deadline and shows the server expiry', () => {
    vi.useFakeTimers();
    vi.setSystemTime(Date.parse(game.serverTime) - 20 * 60_000);
    const pending: Game = {
      ...game, status: 'Pending', revision: 0, sideToMove: null,
      invitationExpiresAt: '2026-01-01T00:00:05Z',
    };
    const expired: Game = { ...pending, status: 'Expired', revision: 1 };
    const expiryRead = new Subject<GameBoard>();
    const board = vi.fn().mockReturnValueOnce(of({ ...initial, game: pending, legalMoves: [] }))
      .mockReturnValueOnce(expiryRead);
    const { page, sync } = open(board);

    vi.advanceTimersByTime(4999);
    expect(board).toHaveBeenCalledTimes(1);
    expect(sync.canAct()).toBe(true);

    vi.advanceTimersByTime(1);
    expect(board).toHaveBeenLastCalledWith(id, 0);
    expect(sync.canAct()).toBe(false);
    expect(sync.submit({ kind: 'invitation', action: 'cancel' })).toBe(false);
    expiryRead.next({ ...initial, game: expired, legalMoves: [] });
    expect(page.status()).toBe('Invitation expired');

    vi.advanceTimersByTime(45000);
    expect(board).toHaveBeenCalledTimes(2);
  });

  it('stops background reads after reconnecting and discovering the final result', () => {
    vi.useFakeTimers();
    const updates = new Subject<number | null>();
    const finished: Game = {
      ...game, status: 'Finished', revision: 23, sideToMove: null, endReason: 'DrawAgreement',
    };
    const board = vi.fn().mockReturnValueOnce(of(initial)).mockReturnValueOnce(of(null))
      .mockReturnValue(of({ ...initial, game: finished, legalMoves: [] }));
    const { page, sync } = open(board, undefined, updates);

    expect(updates.observed).toBe(true);
    vi.advanceTimersByTime(15000);
    expect(board).toHaveBeenCalledTimes(2);
    expect(page.game()?.status).toBe('Active');

    updates.next(null);
    expect(board).toHaveBeenLastCalledWith(id, 22);
    expect(page.game()).toEqual(finished);
    expect(updates.observed).toBe(false);
    expect(sync.canAct()).toBe(false);

    updates.next(24);
    window.dispatchEvent(new Event('online'));
    vi.advanceTimersByTime(45000);
    expect(board).toHaveBeenCalledTimes(3);
  });

  it('reads after reconnect and lets a newer notification supersede an in-flight read', () => {
    const updates = new Subject<number | null>();
    const delayed = new Subject<GameBoard>();
    const move = vi.fn();
    const board = vi.fn()
      .mockReturnValueOnce(of(initial))
      .mockReturnValueOnce(delayed)
      .mockReturnValueOnce(of({ ...initial, game: { ...game, revision: 24 } }))
      .mockReturnValueOnce(of({ ...initial, game: { ...game, revision: 25 } }));
    const { page } = open(board, move, updates);

    updates.next(23);
    updates.next(23);
    updates.next(24);
    delayed.next({ ...initial, game: { ...game, revision: 23 } });
    updates.next(23);
    expect(page.game()?.revision).toBe(24);
    expect(board).toHaveBeenCalledTimes(3);

    updates.next(null);
    expect(board).toHaveBeenLastCalledWith(id, 24);
    expect(page.game()?.revision).toBe(25);
    expect(move).not.toHaveBeenCalled();
  });

  it('ignores a previous game command that finishes after navigation', () => {
    const nextId = '81e318fb-8db7-4262-ad80-780ce7e343fc';
    const nextBoard = { ...initial, game: { ...game, id: nextId, revision: 0 } };
    const params = new BehaviorSubject(convertToParamMap({ id }));
    const pendingMove = new Subject<Game>();
    const move = vi.fn().mockReturnValue(pendingMove);
    const board = vi.fn().mockReturnValueOnce(of(initial)).mockReturnValueOnce(of(nextBoard));
    const { page, sync } = open(board, move, NEVER, params);

    page.play('a7a8q');
    expect(sync.updating()).toBe(true);
    params.next(convertToParamMap({ id: nextId }));
    expect(board).toHaveBeenLastCalledWith(nextId, undefined);

    pendingMove.error(new HttpErrorResponse({ status: 409 }));
    expect(page.game()?.id).toBe(nextId);
    expect(sync.updating()).toBe(false);
    expect(sync.commandError()).toBeNull();
    expect(board).toHaveBeenCalledTimes(2);
  });
});
