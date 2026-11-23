import { DOCUMENT } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { computed, DestroyRef, inject, Injectable, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute } from '@angular/router';
import {
  catchError, distinctUntilChanged, EMPTY, filter, finalize, fromEvent, map, merge,
  Observable, of, Subject, switchMap, take, takeUntil, tap, timer,
} from 'rxjs';
import { AuthSession } from '../identity/auth-session';
import { GameLiveUpdates } from './game-live-updates';
import { Game, GameBoard, GameFinishingAction, gameIdPattern } from './game.models';
import { GamesApi } from './games-api';

type BoardError = 'invalid-link' | 'sign-in-required' | 'not-found' | 'connection';
type BoardActivity = 'idle' | 'sending' | 'refreshing';
export type GameBoardCommand = { kind: 'move'; move: string } | { kind: 'accept-invite' } | GameFinishingAction;

@Injectable()
export class GameBoardSync {
  private readonly api = inject(GamesApi);
  private readonly liveUpdates = inject(GameLiveUpdates);
  private readonly session = inject(AuthSession);
  private readonly window = inject(DOCUMENT).defaultView!;
  private readonly destroyRef = inject(DestroyRef);
  private readonly refreshRequests = new Subject<void>();
  private readonly stopUpdates = new Subject<void>();
  private readonly id = signal('');
  private readonly snapshot = signal<GameBoard | null>(null);
  private readonly activity = signal<BoardActivity>('idle');
  private readonly readError = signal<BoardError | null>(null);
  private readonly actionError = signal<string | null>(null);
  private readonly player = signal<string | null>(null);
  private readonly playerLoadFailed = signal(false);
  // A response belongs to the current route and must not predate the latest command.
  private requestVersion = 0;

  readonly gameId = this.id.asReadonly();
  readonly board = this.snapshot.asReadonly();
  readonly game = computed(() => this.board()?.game);
  readonly boardError = this.readError.asReadonly();
  readonly commandError = this.actionError.asReadonly();
  readonly playerId = this.player.asReadonly();
  readonly playerError = this.playerLoadFailed.asReadonly();
  readonly sessionUnavailable = computed(() => this.session.state() === 'unavailable');
  readonly updating = computed(() => this.activity() !== 'idle');
  readonly canAct = computed(() => !!this.player() && !this.updating()
    && !this.readError() && !this.sessionUnavailable());

  constructor() {
    this.loadPlayer();
    inject(ActivatedRoute).paramMap.pipe(
      map(params => params.get('id')?.toLowerCase() ?? ''),
      distinctUntilChanged(),
      switchMap(id => this.watchBoard(id)),
      takeUntilDestroyed(this.destroyRef),
    ).subscribe();
  }

  retry(): void {
    if (this.playerLoadFailed()) {
      this.loadPlayer();
    }
    this.refreshRequests.next();
  }

  submit(command: GameBoardCommand): boolean {
    const game = this.game();
    if (!game || !this.canAct()) {
      return false;
    }
    const version = ++this.requestVersion;
    this.activity.set('sending');
    this.actionError.set(null);
    this.send(game, command).pipe(
      takeUntilDestroyed(this.destroyRef),
      finalize(() => {
        if (!this.destroyRef.destroyed && version === this.requestVersion) {
          // Even a failed response can belong to a command the server committed.
          this.activity.set('refreshing');
          this.refreshRequests.next();
        }
      }),
    ).subscribe({
      error: (error: HttpErrorResponse) => {
        if (version === this.requestVersion) {
          this.actionError.set(error.error?.code === 'DrawClaimUnavailable' ? 'That draw claim is unavailable.'
            : error.status === 409 ? 'The game changed. Check the current position.'
            : error.status === 422 ? 'That move is illegal in the current position.'
            : 'Could not confirm the action. Check the updated board before trying again.');
        }
      },
    });
    return true;
  }

  private watchBoard(id: string): Observable<GameBoard | null> {
    this.id.set(id);
    this.requestVersion++;
    this.snapshot.set(null);
    this.activity.set('idle');
    this.actionError.set(null);
    this.readError.set(gameIdPattern.test(id) ? null : 'invalid-link');
    if (this.readError()) {
      return EMPTY;
    }

    const firstRead = new Subject<void>();
    const revisions = this.watchRevisions(id, firstRead);
    const online = fromEvent(this.window, 'online');
    return merge(revisions, this.refreshRequests, timer(15000, 15000), online, of(null)).pipe(
      filter(() => this.activity() !== 'sending' && !this.sessionUnavailable()),
      switchMap(() => this.readBoard(id, firstRead)),
      takeUntil(this.stopUpdates),
    );
  }

  private watchRevisions(id: string, firstRead: Observable<void>): Observable<number | null> {
    let latestNotification = -1;
    return firstRead.pipe(
      take(1),
      switchMap(() => this.liveUpdates.watch(id)),
      filter(revision => {
        if (revision === null) {
          return true;
        }
        if (revision <= Math.max(latestNotification, this.game()?.revision ?? -1)) {
          return false;
        }
        latestNotification = revision;
        return true;
      }),
    );
  }

  private readBoard(id: string, firstRead: Subject<void>): Observable<GameBoard | null> {
    const version = this.requestVersion;
    return this.api.board(id, this.game()?.revision).pipe(
      tap(board => {
        if (version !== this.requestVersion) {
          return;
        }
        if (board && board.game.revision >= (this.game()?.revision ?? 0)) {
          this.snapshot.set(board);
        }
        this.readError.set(null);
        this.activity.set('idle');
        firstRead.next();
      }),
      catchError((error: HttpErrorResponse) => {
        // Without an access token, GamesApi returns 401 even when the session can still be restored.
        if (version === this.requestVersion && !this.sessionUnavailable()) {
          this.readError.set(error.status === 401 ? 'sign-in-required'
            : error.status === 404 ? 'not-found' : 'connection');
          if (error.status === 401 || error.status === 404) {
            this.snapshot.set(null);
            this.stopUpdates.next();
          }
        }
        return EMPTY;
      }),
    );
  }

  private send(game: Game, command: GameBoardCommand): Observable<Game> {
    switch (command.kind) {
      case 'move': return this.api.move(game, command.move);
      case 'accept-invite': return this.api.accept(game);
      default: return this.api.finish(game, command);
    }
  }

  private loadPlayer(): void {
    this.playerLoadFailed.set(false);
    this.api.me().pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: player => this.player.set(player.userId),
      error: () => this.playerLoadFailed.set(true),
    });
  }
}
