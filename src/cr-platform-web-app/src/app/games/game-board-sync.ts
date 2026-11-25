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
import {
  Game, GameBoard, GameClock, GameFinishingAction, gameIdPattern, GameInvitationAction, GameRematchAction, turnRemaining,
} from './game.models';
import { GamesApi } from './games-api';
import { guestGames } from './guest-games';
import { deadlineFromServer, remainingUntil, ServerDeadline } from './server-deadline';

type BoardError = 'invalid-link' | 'sign-in-required' | 'not-found' | 'connection';
type BoardActivity = 'idle' | 'sending' | 'refreshing';
type BoardDeadline = { kind: 'invitation' | 'rematch'; time: ServerDeadline };
const boardPollInterval = 15000;
export type GameBoardCommand = { kind: 'move'; move: string }
  | { kind: 'invitation'; action: GameInvitationAction }
  | { kind: 'rematch'; action: GameRematchAction }
  | GameFinishingAction;

@Injectable()
export class GameBoardSync {
  private readonly api = inject(GamesApi);
  private readonly liveUpdates = inject(GameLiveUpdates);
  private readonly session = inject(AuthSession);
  private readonly guest = inject(guestGames);
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
  private readonly actionDeadline = signal<BoardDeadline | null>(null);
  private readonly deadlineElapsed = signal(false);
  private readonly acceptedRematch = signal<string | null>(null);
  // A response belongs to the current route and must not predate the latest command.
  private requestVersion = 0;

  readonly gameId = this.id.asReadonly();
  readonly board = this.snapshot.asReadonly();
  readonly game = computed(() => this.board()?.game);
  readonly boardError = this.readError.asReadonly();
  readonly commandError = this.actionError.asReadonly();
  readonly playerId = this.player.asReadonly();
  readonly playerError = this.playerLoadFailed.asReadonly();
  readonly sessionUnavailable = computed(() => !this.guest && this.session.state() === 'unavailable');
  readonly updating = computed(() => this.activity() !== 'idle');
  readonly startedRematch = this.acceptedRematch.asReadonly();
  private readonly isOpen = computed(() => {
    const status = this.game()?.status;
    return status === 'Pending' || status === 'Active';
  });
  private readonly readyToAct = computed(() => !!this.player() && !this.updating()
    && !this.readError() && !this.sessionUnavailable());
  readonly invitationDeadline = computed(() => this.deadlineFor('invitation'));
  readonly rematchDeadline = computed(() => this.deadlineElapsed() ? null : this.deadlineFor('rematch'));
  readonly canAct = computed(() => this.isOpen() && this.readyToAct()
    && (!this.invitationDeadline() || !this.deadlineElapsed()));
  readonly canRematch = computed(() => !!this.rematchDeadline() && this.readyToAct());

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
    const rematch = command.kind === 'rematch';
    const deadline = this.deadlineFor(rematch ? 'rematch' : 'invitation');
    const allowed = rematch ? this.canRematch() : this.canAct();
    if (!game || !allowed || (deadline && remainingUntil(deadline) <= 0)) {
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
          const changed = command.kind === 'invitation'
            ? 'The invitation changed. Check its current status.' : 'The game changed. Check the current position.';
          this.actionError.set(error.error?.code === 'DrawClaimUnavailable' ? 'That draw claim is unavailable.'
            : error.status === 410 ? (command.kind === 'rematch' ? 'The rematch window has closed.' : 'This invitation has expired.')
            : error.status === 409 ? changed
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
    this.actionDeadline.set(null);
    this.deadlineElapsed.set(false);
    this.acceptedRematch.set(null);
    this.activity.set('idle');
    this.actionError.set(null);
    this.readError.set(gameIdPattern.test(id) ? null : 'invalid-link');
    if (this.readError()) {
      return EMPTY;
    }

    const firstRead = new Subject<void>();
    const deadlineChanges = new Subject<BoardDeadline | null>();
    const turnClock = new Subject<GameClock | null>();
    const deadlineExpiry = deadlineChanges.pipe(
      switchMap(deadline => deadline ? timer(Math.max(0, Math.ceil(remainingUntil(deadline.time)))).pipe(
        tap(() => this.deadlineElapsed.set(true)),
        // Keep watching long enough to discover a rematch accepted just before expiry.
        switchMap(() => deadline.kind === 'rematch'
          ? timer(Math.max(0, Math.ceil(remainingUntil(deadline.time)) + boardPollInterval)) : of(null)),
      ) : EMPTY),
    );
    const clockExpiry = turnClock.pipe(
      distinctUntilChanged((previous, current) => previous?.turnDeadline === current?.turnDeadline),
      switchMap(clock => clock?.turnDeadline ? timer(Math.max(0, turnRemaining(clock)) + 50) : EMPTY),
    );
    const revisions = this.watchRevisions(id, firstRead);
    const online = fromEvent(this.window, 'online');
    return merge(revisions, this.refreshRequests, deadlineExpiry, clockExpiry,
      timer(boardPollInterval, boardPollInterval), online, of(null)).pipe(
      filter(() => this.activity() !== 'sending' && !this.sessionUnavailable()),
      switchMap(() => this.readBoard(id, firstRead, deadlineChanges, turnClock)),
      takeUntil(this.stopUpdates),
    );
  }

  private watchRevisions(id: string, firstRead: Observable<void>): Observable<number | null> {
    let latestNotification = -1;
    return firstRead.pipe(
      take(1),
      switchMap(() => this.guest ? this.liveUpdates.watch(id, () => this.api.guestLiveProof()) : this.liveUpdates.watch(id)),
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

  private readBoard(id: string, firstRead: Subject<void>,
    deadlineChanges: Subject<BoardDeadline | null>, turnClock: Subject<GameClock | null>
  ): Observable<GameBoard | null> {
    const version = this.requestVersion;
    const readStartedAt = performance.now();
    return this.api.board(id, this.game()?.revision).pipe(
      tap(board => {
        if (version !== this.requestVersion) {
          return;
        }
        const previous = this.game();
        if (board && board.game.revision >= (previous?.revision ?? 0)) {
          const game = board.game;
          const deadline = this.readDeadline(game);
          this.actionDeadline.set(deadline);
          this.deadlineElapsed.set(deadline !== null && remainingUntil(deadline.time) <= 0);
          this.snapshot.set(board);
          deadlineChanges.next(deadline);
          turnClock.next(game.clock);
          if (previous && !previous.rematchGameId && game.rematchGameId) {
            this.acceptedRematch.set(game.rematchGameId);
          }
        }
        this.readError.set(null);
        this.activity.set('idle');
        const game = this.game();
        const rematch = this.deadlineFor('rematch');
        // A read started before expiry may have missed a later acceptance.
        const watchingRematch = rematch && readStartedAt < rematch.monotonicAt + boardPollInterval;
        if (game && !this.isOpen() && !watchingRematch) {
          this.stopUpdates.next();
        } else {
          firstRead.next();
        }
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

  private readDeadline(game: Game): BoardDeadline | null {
    const kind = game.status === 'Pending' ? 'invitation' : 'rematch';
    const expiresAt = kind === 'invitation' ? game.invitationExpiresAt : game.rematchExpiresAt;
    const time = deadlineFromServer(expiresAt, game.serverTime, performance.now());
    return time && !game.rematchGameId ? { kind, time } : null;
  }

  private deadlineFor(kind: BoardDeadline['kind']): ServerDeadline | null {
    const deadline = this.actionDeadline();
    return deadline?.kind === kind ? deadline.time : null;
  }

  private send(game: Game, command: GameBoardCommand): Observable<Game> {
    switch (command.kind) {
      case 'move': return this.api.move(game, command.move);
      case 'invitation': return this.api.updateInvitation(game, command.action);
      case 'rematch': return this.api.updateRematch(game, command.action);
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
