import { Component, computed, inject } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { AuthSession } from '../identity/auth-session';
import { GamePage } from './game-page';
import { GameReplayPage } from './game-replay-page';
import { GamesPage } from './games-page';
import { GuestGameSessionPage } from './guest-game-session-page';
import { InvitationReturn, invitationReturn } from './invitation-return';
import { LobbyPage } from './lobby-page';

@Component({
  imports: [GamePage, GameReplayPage, GamesPage, GuestGameSessionPage, LobbyPage, RouterLink],
  template: `
    @for (login of activeLogin(); track login) {
      @if (lobbyId; as lobby) {
        @for (id of [lobby]; track id) {
          <cr-lobby-page />
        }
      } @else if (replayId; as replay) {
        @for (id of [replay]; track id) {
          <cr-game-replay-page [gameId]="id" />
        }
      } @else if (gameId) {
        <cr-game-page />
      } @else {
        <cr-games-page />
      }
    } @empty {
      @if (session.state() === 'checking') {
        <p role="status">Checking your session…</p>
      } @else if (lobbyId) {
        <cr-guest-game-session-page />
      } @else if (session.state() === 'unavailable') {
        <a routerLink="/session-unavailable" [queryParams]="returnParams">Restore session</a>
      } @else {
        @if (session.state() === 'sign-in-required') {
          <p role="status">We couldn’t verify your session after an interrupted request. Sign in again to continue.</p>
        } @else {
          <p role="status">Sign in to see your games.</p>
        }
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

  protected get lobbyId(): string | null {
    return this.route.snapshot.paramMap.get('lobbyId');
  }

  protected get replayId(): string | null {
    return this.route.snapshot.paramMap.get('replayId');
  }

  protected get returnParams(): InvitationReturn | undefined {
    return invitationReturn(this.route.snapshot.paramMap, 'id');
  }
}
