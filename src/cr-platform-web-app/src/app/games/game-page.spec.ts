import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
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
    fen: '7k/P7/8/8/8/8/8/7K w - - 0 12', sideToMove: 'White', winner: null, endReason: null, drawOfferedBy: null,
  };
  const initial: GameBoard = { game, moves: [], legalMoves: ['a7a8q', 'a7a8r', 'a7a8b', 'a7a8n'], drawClaims: [] };

  function open(board: ReturnType<typeof vi.fn>, move = vi.fn().mockReturnValue(of(game)),
    updates: Observable<number | null> = NEVER, params = of(convertToParamMap({ id }))) {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: AuthSession, useValue: { state: () => 'authenticated' } },
        { provide: GameLiveUpdates, useValue: { watch: () => updates } },
        { provide: ActivatedRoute, useValue: { paramMap: params } },
      ],
    }).overrideComponent(GamePage, { set: { providers: [
      GameBoardSync,
      { provide: GamesApi, useValue: { me: () => of({ userId: 'white' }), board, move } },
    ] } });
    const fixture = TestBed.createComponent(GamePage);
    return { page: fixture.componentInstance, sync: fixture.debugElement.injector.get(GameBoardSync) };
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

  it('explains a resignation draw on the board', () => {
    const finished: Game = {
      ...game, status: 'Finished', sideToMove: null, endReason: 'ResignationNoPossibleMate',
    };
    const { page } = open(vi.fn().mockReturnValue(of({ ...initial, game: finished, legalMoves: [] })));
    expect(page.status()).toBe('Draw · Insufficient mating material after resignation');
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
