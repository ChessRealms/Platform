import { Component, inject } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { GamePage } from './game-page';
import { GameReplayPage } from './game-replay-page';
import { guestGames } from './guest-games';
import { LobbyPage } from './lobby-page';

@Component({
  selector: 'cr-guest-game-session-page',
  imports: [GamePage, GameReplayPage, LobbyPage],
  providers: [{ provide: guestGames, useValue: true }],
  template: `
    @if (lobbyId; as lobby) {
      @for (id of [lobby]; track id) {
        <cr-lobby-page />
      }
    } @else if (replayId; as replay) {
      @for (id of [replay]; track id) {
        <cr-game-replay-page [gameId]="id" />
      }
    } @else {
      <cr-game-page />
    }
  `,
})
export class GuestGameSessionPage {
  private readonly route = inject(ActivatedRoute);

  protected get lobbyId(): string | null {
    return this.route.snapshot.paramMap.get('lobbyId');
  }

  protected get replayId(): string | null {
    return this.route.snapshot.paramMap.get('replayId');
  }
}
