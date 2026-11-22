import { inject, Injectable } from '@angular/core';
import { HttpTransportType, HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { Observable } from 'rxjs';
import { AuthSession } from '../identity/auth-session';

@Injectable({ providedIn: 'root' })
export class GameLiveUpdates {
  private readonly session = inject(AuthSession);

  // A null emission requests a read after joining or rejoining the game.
  watch(gameId: string): Observable<number | null> {
    return new Observable(subscriber => {
      const connection = new HubConnectionBuilder()
        .withUrl('/api/games/live', {
          transport: HttpTransportType.WebSockets,
          skipNegotiation: true,
          accessTokenFactory: async () => await this.session.getAccessToken() ?? '',
        })
        .configureLogging(LogLevel.None)
        .build();
      let retry: ReturnType<typeof setTimeout> | undefined;
      let retryDelay = 2000;

      const scheduleReconnect = () => {
        if (!subscriber.closed && retry === undefined) {
          retry = setTimeout(() => {
            retry = undefined;
            void connect();
          }, retryDelay);
          retryDelay = Math.min(retryDelay * 2, 30000);
        }
      };

      const connect = async () => {
        try {
          await connection.start();
          if (!subscriber.closed) {
            await connection.invoke('Subscribe', gameId);
            retryDelay = 2000;
            subscriber.next(null);
          }
        } catch {
          await connection.stop();
          scheduleReconnect();
        }
      };

      connection.on('GameChanged', (id: string, revision: number) => {
        if (id === gameId && Number.isSafeInteger(revision) && revision >= 0) {
          subscriber.next(revision);
        }
      });
      connection.onclose(scheduleReconnect);
      void connect();

      return () => {
        clearTimeout(retry);
        void connection.stop();
      };
    });
  }
}
