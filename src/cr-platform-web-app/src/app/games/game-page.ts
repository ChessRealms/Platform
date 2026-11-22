import { DOCUMENT } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { catchError, distinctUntilChanged, EMPTY, exhaustMap, finalize, map, merge, Observable, Subject, switchMap, timer } from 'rxjs';
import { ChessBoard } from './chess-board';
import { Game, GameBoard, gameIdPattern } from './game.models';
import { GamesApi } from './games-api';

const promotionNames: Record<string, string> = { q: 'Queen', r: 'Rook', b: 'Bishop', n: 'Knight' };
const finishReasons: Record<string, string> = {
  Checkmate: 'Checkmate', Stalemate: 'Stalemate', DeadPosition: 'Dead position',
  FivefoldRepetition: 'Fivefold repetition', SeventyFiveMoveRule: '75-move rule',
};

@Component({
  imports: [ChessBoard, RouterLink],
  templateUrl: './game-page.html',
  styleUrl: './game-page.css',
})
export class GamePage {
  private readonly api = inject(GamesApi);
  private readonly document = inject(DOCUMENT);
  private readonly destroyRef = inject(DestroyRef);
  private readonly refresh = new Subject<void>();
  protected currentId: string | null = null;
  private commandVersion = 0;
  readonly playerId = signal<string | null>(null);
  readonly board = signal<GameBoard | null>(null);
  readonly selected = signal<string | null>(null);
  readonly promotion = signal<string[]>([]);
  readonly busy = signal(false);
  readonly synchronizing = signal(false);
  readonly unavailable = signal(false);
  readonly signInRequired = signal(false);
  readonly connectionError = signal<string | null>(null);
  readonly playerError = signal(false);
  readonly commandError = signal<string | null>(null);
  readonly promotionNames = promotionNames;

  readonly game = computed(() => this.board()?.game);
  readonly color = computed(() => this.game()?.blackPlayerId === this.playerId() ? 'Black' : 'White');
  readonly canAct = computed(() => !!this.playerId() && !this.busy() && !this.synchronizing() && !this.connectionError());
  readonly targets = computed(() => new Set((this.board()?.legalMoves ?? [])
    .filter(move => move.slice(0, 2) === this.selected()).map(move => move.slice(2, 4))));
  readonly lastMove = computed(() => {
    const move = this.board()?.moves.at(-1);
    return move ? [move.slice(0, 2), move.slice(2, 4)] : [];
  });
  readonly moveRows = computed(() => {
    const moves = this.board()?.moves ?? [];
    return moves.filter((_, i) => i % 2 === 0).map((white, i) => ({ white, black: moves[i * 2 + 1] }));
  });
  readonly status = computed(() => {
    const game = this.game();
    if (!game) {
      return 'Loading game…';
    }
    if (game.status === 'Pending') {
      return this.color() === 'Black' ? 'Invitation to play Black' : 'Waiting for your opponent';
    }
    if (game.status === 'Finished') {
      const reason = game.endReason ? finishReasons[game.endReason] : undefined;
      return (game.winner ? `${game.winner} wins` : 'Draw') + (reason ? ` · ${reason}` : '');
    }
    return game.sideToMove === this.color() ? 'Your turn' : `${game.sideToMove} to move`;
  });

  constructor() {
    this.loadPlayer();
    inject(ActivatedRoute).paramMap.pipe(
      map(params => params.get('id')?.toLowerCase() ?? ''),
      distinctUntilChanged(),
      switchMap(id => {
        this.currentId = id;
        this.commandVersion++;
        this.board.set(null);
        this.clearSelection();
        this.connectionError.set(null);
        this.commandError.set(null);
        this.busy.set(false);
        this.synchronizing.set(false);
        this.signInRequired.set(false);
        this.unavailable.set(!gameIdPattern.test(id));
        return this.unavailable() ? EMPTY : merge(timer(0, 2500), this.refresh).pipe(
          exhaustMap(() => this.busy() || this.unavailable() ? EMPTY : this.readBoard(id)),
        );
      }),
      takeUntilDestroyed(this.destroyRef),
    ).subscribe();
  }

  retry(): void {
    if (this.playerError()) {
      this.loadPlayer();
    }
    this.refresh.next();
  }

  gameLink(game: Game): string {
    return new URL('/games/' + game.id, this.document.baseURI).href;
  }

  select(square: string): void {
    if (!this.canAct() || this.promotion().length > 0) {
      return;
    }
    const moves = this.board()?.legalMoves ?? [];
    const selected = this.selected();
    const choices = selected ? moves.filter(move => move.startsWith(selected + square)) : [];
    if (choices.length > 1) {
      this.promotion.set(choices);
    } else if (choices.length === 1) {
      this.play(choices[0]);
    } else {
      this.selected.set(square !== this.selected() && moves.some(move => move.startsWith(square)) ? square : null);
    }
  }

  clearSelection(): void {
    this.selected.set(null);
    this.promotion.set([]);
  }

  play(move: string): void {
    this.command(game => this.api.move(game, move));
  }

  accept(): void {
    this.command(game => this.api.accept(game));
  }

  private loadPlayer(): void {
    this.playerError.set(false);
    this.api.me().pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: player => this.playerId.set(player.userId),
      error: () => this.playerError.set(true),
    });
  }

  private readBoard(id: string): Observable<unknown> {
    const version = this.commandVersion;
    return this.api.board(id, this.game()?.revision).pipe(
      map(board => {
        if (version !== this.commandVersion) {
          return;
        }
        if (board && board.game.revision >= (this.game()?.revision ?? 0)) {
          this.board.set(board);
          this.clearSelection();
        }
        this.connectionError.set(null);
        this.synchronizing.set(false);
      }),
      catchError(error => {
        if (version === this.commandVersion) {
          if (error.status === 404 || error.status === 401) {
            this.unavailable.set(true);
            this.board.set(null);
          }
          this.signInRequired.set(error.status === 401);
          this.connectionError.set(error.status === 401 ? 'Sign in to see this game.'
            : error.status === 404 ? 'This game is unavailable.'
            : 'Connection interrupted. The board will retry automatically.');
        }
        return EMPTY;
      }),
    );
  }

  private command(send: (game: Game) => Observable<Game>): void {
    const game = this.game();
    if (!game || !this.canAct()) {
      return;
    }
    const commandVersion = ++this.commandVersion;
    this.busy.set(true);
    this.commandError.set(null);
    this.clearSelection();
    send(game).pipe(
      takeUntilDestroyed(this.destroyRef),
      finalize(() => {
        if (this.currentId === game.id && commandVersion === this.commandVersion) {
          this.synchronizing.set(true);
          this.busy.set(false);
          this.refresh.next();
        }
      }),
    ).subscribe({
      error: (error: HttpErrorResponse) => {
        if (this.currentId === game.id && commandVersion === this.commandVersion) {
          this.commandError.set(error.status === 409 ? 'The game changed. Check the current position.'
            : error.status === 422 ? 'That move is illegal in the current position.'
            : 'Could not confirm the action. Check the updated board before trying again.');
        }
      },
    });
  }
}
