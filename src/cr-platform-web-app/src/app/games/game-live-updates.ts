import { inject, Injectable } from '@angular/core';
import { HttpTransportType, HubConnection, HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { Observable } from 'rxjs';
import { AuthSession } from '../identity/auth-session';

@Injectable({ providedIn: 'root' })
export class GameLiveUpdates {
  private readonly session = inject(AuthSession);

  // A null emission requests a read after joining or rejoining the game.
  watch(gameId: string, guestProof?: () => Promise<string>): Observable<number | null> {
    return this.watchSubscription<number>(connection => connection.invoke('Subscribe', gameId),
      (connection, emit) => connection.on('GameChanged', (id: string, revision: number) => {
        if (id === gameId && Number.isSafeInteger(revision) && revision >= 0) {
          emit(revision);
        }
      }), guestProof);
  }

  watchMyGames(): Observable<null> {
    return this.watchSubscription<never>(connection => connection.invoke('SubscribeMyGames'),
      (connection, emit) => connection.on('MyGamesChanged', () => emit(null)));
  }

  private watchSubscription<T>(subscribe: (connection: HubConnection) => Promise<void>,
    bindEvents: (connection: HubConnection, emit: (value: T | null) => void) => void,
    guestProof?: () => Promise<string>,
  ): Observable<T | null> {
    const login = this.session.loginKey();
    return new Observable(subscriber => {
      const connection = new HubConnectionBuilder()
        .withUrl('/api/games/live', {
          transport: HttpTransportType.WebSockets,
          skipNegotiation: true,
          accessTokenFactory: guestProof ?? (async () => await this.session.getAccessToken(login) ?? ''),
        })
        .configureLogging(LogLevel.None)
        .build();
      let retry: ReturnType<typeof setTimeout> | undefined;
      let retryDelay = 2000;

      const current = () => !subscriber.closed && (!!guestProof || login === this.session.loginKey());
      const emit = (value: T | null) => {
        if (current()) {
          subscriber.next(value);
        }
      };
      const scheduleReconnect = () => {
        if (current() && retry === undefined) {
          retry = setTimeout(() => {
            retry = undefined;
            void connect();
          }, retryDelay);
          retryDelay = Math.min(retryDelay * 2, 30000);
        }
      };

      const connect = async () => {
        if (!current()) {
          return;
        }
        try {
          await connection.start();
          if (current()) {
            await subscribe(connection);
            retryDelay = 2000;
            emit(null);
          } else {
            await connection.stop();
          }
        } catch {
          await connection.stop();
          scheduleReconnect();
        }
      };

      bindEvents(connection, emit);
      connection.onclose(scheduleReconnect);
      void connect();

      return () => {
        clearTimeout(retry);
        void connection.stop();
      };
    });
  }
}
