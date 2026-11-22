import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { vi } from 'vitest';
import { GamePage } from './game-page';
import { Game, GameBoard } from './game.models';
import { GamesApi } from './games-api';

describe('GamePage', () => {
  const id = '1ae1fd73-7d50-43e2-bc5c-f9ef232fcdad';
  const game: Game = {
    id, whitePlayerId: 'white', blackPlayerId: 'black', status: 'Active', revision: 22,
    fen: '7k/P7/8/8/8/8/8/7K w - - 0 12', sideToMove: 'White', winner: null, endReason: null,
  };
  const initial: GameBoard = { game, moves: [], legalMoves: ['a7a8q', 'a7a8r', 'a7a8b', 'a7a8n'] };

  function open(board: ReturnType<typeof vi.fn>, move = vi.fn().mockReturnValue(of(game))): GamePage {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: GamesApi, useValue: { me: () => of({ userId: 'white' }), board, move } },
        { provide: ActivatedRoute, useValue: { paramMap: of(convertToParamMap({ id })) } },
      ],
    });
    const page = TestBed.createComponent(GamePage).componentInstance;
    page.retry();
    return page;
  }

  it('uses server legal moves and requires an explicit promotion choice', () => {
    const move = vi.fn().mockReturnValue(of(game));
    const page = open(vi.fn().mockReturnValue(of(initial)), move);
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
    const page = open(board, move);
    page.select('a7');
    page.select('a8');
    page.play('a7a8q');
    expect(move).toHaveBeenCalledTimes(1);
    expect(board).toHaveBeenLastCalledWith(id, 22);
    expect(page.game()?.revision).toBe(23);
    expect(page.promotion()).toEqual([]);
    expect(page.commandError()).toContain('The game changed');
  });
});
