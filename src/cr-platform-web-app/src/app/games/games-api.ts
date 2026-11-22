import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { defer, Observable, switchMap } from 'rxjs';
import { AuthSession } from '../identity/auth-session';
import { IdentityApiClient } from '../identity/identity-api.client';
import { CurrentUser } from '../identity/identity-api.models';
import { Game, GameBoard, GameFinishingAction, GameListPage, GameStatus } from './game.models';

@Injectable({ providedIn: 'root' })
export class GamesApi {
  private readonly http = inject(HttpClient);
  private readonly session = inject(AuthSession);
  private readonly identity = inject(IdentityApiClient);

  me(): Observable<CurrentUser> {
    return this.authorized(token => this.identity.me(token));
  }

  create(opponentId: string): Observable<Game> {
    return this.authorized(token =>
      this.http.post<Game>('/api/games', { opponentId }, this.options(token)));
  }

  list(status: GameStatus, before: string | null): Observable<GameListPage> {
    return this.authorized(token => this.http.get<GameListPage>('/api/games', {
      ...this.options(token),
      params: before ? { status, before } : { status },
    }));
  }

  board(id: string, revision?: number): Observable<GameBoard | null> {
    return this.authorized(token => this.http.get<GameBoard | null>(`/api/games/${encodeURIComponent(id)}/board`, {
      ...this.options(token),
      params: revision === undefined ? {} : { afterRevision: revision },
    }));
  }

  accept(game: Game): Observable<Game> {
    return this.authorized(token => this.http.post<Game>(`/api/games/${encodeURIComponent(game.id)}/accept`,
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
    return defer(() => this.session.getAccessToken()).pipe(switchMap(token => {
      if (!token) {
        throw new HttpErrorResponse({ status: 401 });
      }
      return request(token);
    }));
  }

  private options(token: string) {
    return { headers: { Authorization: `Bearer ${token}` }, timeout: 10000 };
  }
}
