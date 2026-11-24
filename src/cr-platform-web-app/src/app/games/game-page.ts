import { DOCUMENT } from '@angular/common';
import { Component, computed, effect, inject, linkedSignal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { ChessBoard } from './chess-board';
import { GameBoardCommand, GameBoardSync } from './game-board-sync';
import { GameFinishingActions } from './game-finishing-actions';
import {
  finishReasons, Game, GameBoard, GameFinishingAction, GameInvitationAction, GameRematchAction, invitationOutcome,
} from './game.models';
import { GamesApi } from './games-api';
import { InvitationCountdown } from './invitation-countdown';

const promotionNames: Record<string, string> = { q: 'Queen', r: 'Rook', b: 'Bishop', n: 'Knight' };

@Component({
  selector: 'cr-game-page',
  providers: [GamesApi, GameBoardSync],
  imports: [ChessBoard, GameFinishingActions, InvitationCountdown, RouterLink],
  templateUrl: './game-page.html',
  styleUrl: './game-page.css',
})
export class GamePage {
  protected readonly sync = inject(GameBoardSync);
  private readonly document = inject(DOCUMENT);
  private readonly router = inject(Router);

  readonly game = this.sync.game;
  readonly selected = linkedSignal<GameBoard | null, string | null>({
    source: this.sync.board,
    computation: () => null,
  });
  readonly promotion = linkedSignal<GameBoard | null, string[]>({
    source: this.sync.board,
    computation: () => [],
  });
  readonly promotionNames = promotionNames;
  protected readonly boardErrorMessages = {
    'sign-in-required': 'Sign in to see this game.',
    'not-found': 'This game is unavailable.',
    connection: 'Connection interrupted. The board will retry automatically.',
  };
  readonly color = computed(() => this.game()?.blackPlayerId === this.sync.playerId() ? 'Black' : 'White');
  readonly targets = computed(() => new Set((this.sync.board()?.legalMoves ?? [])
    .filter(move => move.slice(0, 2) === this.selected()).map(move => move.slice(2, 4))));
  readonly lastMove = computed(() => {
    const move = this.sync.board()?.moves.at(-1);
    return move ? [move.slice(0, 2), move.slice(2, 4)] : [];
  });
  readonly moveRows = computed(() => {
    const moves = this.sync.board()?.moves ?? [];
    return moves.filter((_, i) => i % 2 === 0).map((white, i) => ({ white, black: moves[i * 2 + 1] }));
  });
  protected readonly closedInvitation = computed(() => invitationOutcome(this.game()?.status));
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
    return this.closedInvitation() ?? (game.sideToMove === this.color() ? 'Your turn' : `${game.sideToMove} to move`);
  });

  constructor() {
    effect(() => {
      const rematchId = this.sync.startedRematch();
      if (rematchId) {
        void this.router.navigate(['/games', rematchId]);
      }
    });
  }

  gameLink(game: Game): string {
    return new URL('/games/' + game.id, this.document.baseURI).href;
  }

  select(square: string): void {
    if (!this.sync.canAct() || this.promotion().length > 0) {
      return;
    }
    const moves = this.sync.board()?.legalMoves ?? [];
    const selected = this.selected();
    const choices = selected ? moves.filter(move => move.startsWith(selected + square)) : [];
    if (choices.length > 1) {
      this.promotion.set(choices);
    } else if (choices.length === 1) {
      this.play(choices[0]);
    } else {
      this.selected.set(square !== selected && moves.some(move => move.startsWith(square)) ? square : null);
    }
  }

  clearSelection(): void {
    this.selected.set(null);
    this.promotion.set([]);
  }

  play(move: string): void {
    this.submit({ kind: 'move', move });
  }

  protected updateInvitation(action: GameInvitationAction): void {
    this.submit({ kind: 'invitation', action });
  }

  protected updateRematch(action: GameRematchAction): void {
    this.submit({ kind: 'rematch', action });
  }

  finish(action: GameFinishingAction): void {
    this.submit(action);
  }

  private submit(command: GameBoardCommand): void {
    if (this.sync.submit(command)) {
      this.clearSelection();
    }
  }
}
