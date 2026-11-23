import { Component, computed, inject } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { AuthSession } from '../identity/auth-session';
import { GamePage } from './game-page';
import { gameIdPattern } from './game.models';
import { GamesPage } from './games-page';

@Component({
  imports: [GamePage, GamesPage, RouterLink],
  template: `
    @for (login of activeLogin(); track login) {
      @if (gameId) {
        <cr-game-page />
      } @else {
        <cr-games-page />
      }
    } @empty {
      @if (session.state() === 'checking') {
        <p role="status">Checking your session…</p>
      } @else if (session.state() === 'unavailable') {
        <a routerLink="/session-unavailable" [queryParams]="returnParams">Restore session</a>
      } @else {
        <p role="status">Sign in to see your games.</p>
        <a routerLink="/login" [queryParams]="returnParams">Sign in</a>
      }
    }
  `,
})
export class GameSessionPage {
  protected readonly session = inject(AuthSession);
  private readonly route = inject(ActivatedRoute);
  // A new login owns a fresh component, including its HTTP requests and live subscription.
  protected readonly activeLogin = computed(() => {
    const key = this.session.loginKey();
    return key ? [key] : [];
  });

  protected get gameId(): string | null {
    return this.route.snapshot.paramMap.get('id');
  }

  protected get returnParams(): { gameId: string } | null {
    const gameId = this.gameId;
    return gameId && gameIdPattern.test(gameId) ? { gameId } : null;
  }
}
