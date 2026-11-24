import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { defer, EMPTY, filter, Observable, switchMap } from 'rxjs';
import { AuthSession } from '../identity/auth-session';
import { IdentityApiClient } from '../identity/identity-api.client';
import { CurrentUser } from '../identity/identity-api.models';
import { GameReplay } from './game-replay.models';
import {
  Game, GameBoard, GameFinishingAction, GameInvitationAction, GameListFilter, GameListPage, GameRematchAction,
  GameTimeControl, PrivateLobby,
} from './game.models';

export type GameCreationOptions = { requestId: string; timeControl?: GameTimeControl };
export type GameCreation = GameCreationOptions & (
  { opponentUsername: string; opponentId?: never } | { opponentId: string; opponentUsername?: never }
);

@Injectable()
export class GamesApi {
  private readonly http = inject(HttpClient);
  private readonly session = inject(AuthSession);
  private readonly login = this.session.loginKey();
  private readonly identity = inject(IdentityApiClient);

  me(): Observable<CurrentUser> {
    return this.authorized(token => this.identity.me(token));
  }

  create(attempt: GameCreation): Observable<Game> {
    return this.authorized(token =>
      this.http.post<Game>('/api/games', attempt, this.options(token)));
  }

  list(status: GameListFilter, before: string | null): Observable<GameListPage> {
    return this.authorized(token => this.http.get<GameListPage>('/api/games', {
      ...this.options(token),
      params: before ? { status, before } : { status },
    }));
  }

  createLobby(attempt: GameCreationOptions): Observable<PrivateLobby> {
    return this.authorized(token => this.http.post<PrivateLobby>('/api/lobbies', attempt, this.options(token)));
  }

  lobby(id: string): Observable<PrivateLobby> {
    return this.authorized(token => this.http.get<PrivateLobby>(`/api/lobbies/${encodeURIComponent(id)}`, this.options(token)));
  }

  updateLobby(lobby: PrivateLobby, action: 'join' | 'cancel'): Observable<PrivateLobby> {
    return this.authorized(token => this.http.post<PrivateLobby>(`/api/lobbies/${encodeURIComponent(lobby.id)}/${action}`,
      { expectedRevision: lobby.revision }, this.options(token)));
  }

  board(id: string, revision?: number): Observable<GameBoard | null> {
    return this.authorized(token => this.http.get<GameBoard | null>(`/api/games/${encodeURIComponent(id)}/board`, {
      ...this.options(token),
      params: revision === undefined ? {} : { afterRevision: revision },
    }));
  }

  replay(id: string): Observable<GameReplay> {
    return this.authorized(token => this.http.get<GameReplay>(`/api/games/${encodeURIComponent(id)}/replay`,
      this.options(token)));
  }

  updateInvitation(game: Game, action: GameInvitationAction): Observable<Game> {
    return this.authorized(token => this.http.post<Game>(`/api/games/${encodeURIComponent(game.id)}/${action}`,
      { expectedRevision: game.revision }, this.options(token)));
  }

  updateRematch(game: Game, action: GameRematchAction): Observable<Game> {
    return this.authorized(token => this.http.post<Game>(`/api/games/${encodeURIComponent(game.id)}/rematch/${action}`,
      { expectedRevision: game.revision }, this.options(token)));
  }

  move(game: Game, move: string): Observable<Game> {
    return this.authorized(token => this.http.post<Game>(`/api/games/${encodeURIComponent(game.id)}/moves`,
      { move, expectedRevision: game.revision }, this.options(token)));
  }

  finish(game: Game, action: GameFinishingAction): Observable<Game> {
    const path = action.kind === 'resign' ? 'resign' : `draw/${action.kind}`;
    const body = action.kind === 'claim'
      ? { expectedRevision: game.revision, reason: action.reason, intendedMove: action.intendedMove }
      : { expectedRevision: game.revision };
    return this.authorized(token => this.http.post<Game>(`/api/games/${encodeURIComponent(game.id)}/${path}`,
      body, this.options(token)));
  }

  private authorized<T>(request: (token: string) => Observable<T>): Observable<T> {
    return defer(() => this.session.getAccessToken(this.login)).pipe(switchMap(token => {
      if (this.login !== this.session.loginKey()) {
        return EMPTY;
      }
      if (!token) {
        throw new HttpErrorResponse({ status: 401 });
      }
      return request(token).pipe(filter(() => this.login === this.session.loginKey()));
    }));
  }

  private options(token: string) {
    return { headers: { Authorization: `Bearer ${token}` }, timeout: 10000 };
  }
}
