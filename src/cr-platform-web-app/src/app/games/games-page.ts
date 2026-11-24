import { DatePipe, DOCUMENT } from '@angular/common';
import { Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { catchError, EMPTY, finalize, Subject, switchMap } from 'rxjs';
import { AuthSession } from '../identity/auth-session';
import { finishReasons, gameIdPattern, GameListItem } from './game.models';
import { GameCreation, GamesApi } from './games-api';

const gameFilters = [
  { status: 'Pending', label: 'Invitations', empty: 'No pending invitations.' },
  { status: 'Active', label: 'Active', empty: 'No active games.' },
  { status: 'Finished', label: 'Finished', empty: 'No finished games.' },
] as const;

type GameFilter = (typeof gameFilters)[number];
const maxUsernameLength = 256;

const pendingCreationKey = 'chessrealms.pending-game-creation';

@Component({
  selector: 'cr-games-page',
  providers: [GamesApi],
  imports: [DatePipe, FormsModule, RouterLink],
  templateUrl: './games-page.html',
  styleUrl: './games-page.css',
})
export class GamesPage {
  private readonly api = inject(GamesApi);
  private readonly session = inject(AuthSession);
  private readonly login = this.session.loginKey();
  private readonly window = inject(DOCUMENT).defaultView!;
  private readonly destroyRef = inject(DestroyRef);
  private readonly router = inject(Router);
  private readonly listRequests = new Subject<string | null>();
  protected readonly filters = gameFilters;
  protected readonly filter = signal<GameFilter>(gameFilters[0]);
  protected readonly games = signal<GameListItem[]>([]);
  protected readonly nextCursor = signal<string | null>(null);
  protected readonly listLoading = signal(false);
  protected readonly listError = signal<string | null>(null);
  protected readonly username = signal<string | null>(null);
  protected readonly usernameError = signal<string | null>(null);
  protected readonly maxUsernameLength = maxUsernameLength;
  readonly error = signal<string | null>(null);
  readonly busy = signal(false);
  protected readonly pendingCreation = signal(this.readPendingCreation());
  opponentUsername = this.pendingCreation()?.opponentUsername ?? '';

  constructor() {
    this.listRequests.pipe(
      switchMap(before => {
        this.listLoading.set(true);
        this.listError.set(null);
        return this.api.list(this.filter().status, before).pipe(
          catchError(() => {
            this.listError.set('Could not load your games. Try again.');
            return EMPTY;
          }),
          finalize(() => this.listLoading.set(false)),
        );
      }),
      takeUntilDestroyed(this.destroyRef),
    ).subscribe(page => {
      this.games.update(items => [...items, ...page.items]);
      this.nextCursor.set(page.nextCursor);
    });
    this.refresh();

    this.api.me().pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: user => this.username.set(user.username),
      error: () => this.usernameError.set('Could not load your username. Reload the page to try again.'),
    });
  }

  protected show(filter: GameFilter): void {
    if (filter !== this.filter()) {
      this.filter.set(filter);
      this.refresh();
    }
  }

  protected refresh(): void {
    this.games.set([]);
    this.nextCursor.set(null);
    this.listRequests.next(null);
  }

  protected loadMore(): void {
    const before = this.nextCursor();
    if (before && !this.listLoading()) {
      this.listRequests.next(before);
    }
  }

  protected statusText(game: GameListItem): string {
    if (game.status === 'Pending') {
      return game.color === 'Black' ? 'Invitation received' : 'Waiting for acceptance';
    }
    if (game.status === 'Active') {
      return game.isYourTurn ? 'Your turn' : 'Opponent’s turn';
    }
    const result = game.winner === null ? 'Draw' : game.winner === game.color ? 'Won' : 'Lost';
    const reason = game.endReason ? finishReasons[game.endReason] : undefined;
    return reason ? `${result} · ${reason}` : result;
  }

  create(): void {
    if (this.busy()) {
      return;
    }
    const pending = this.pendingCreation();
    const opponentUsername = this.opponentUsername.trim();
    if (!pending && (!opponentUsername || opponentUsername.length > maxUsernameLength)) {
      this.error.set('Enter a username of up to 256 characters.');
      return;
    }
    const attempt = pending ?? { opponentUsername, requestId: this.window.crypto.randomUUID() };
    this.rememberCreation(attempt);
    this.error.set(null);
    this.busy.set(true);
    this.api.create(attempt).pipe(
      takeUntilDestroyed(this.destroyRef),
      finalize(() => this.busy.set(false)),
    ).subscribe({
      next: game => {
        this.rememberCreation(null);
        void this.router.navigate(['/games', game.id]);
      },
      error: error => {
        if (error.status === 409) {
          this.rememberCreation(null);
          this.error.set('This request ID has already been used for a different invitation. Start a new invitation.');
        } else if (error.status === 400) {
          this.rememberCreation(null);
          if (attempt.opponentId) {
            this.error.set('Could not retry that invitation. Enter your opponent’s username to send a new one.');
          } else if (error.error?.code === 'InvalidPlayers') {
            this.error.set('Enter another player’s username.');
          } else {
            this.error.set('That player is unavailable. Check the username and ask them to confirm their email.');
          }
        }
      },
    });
  }

  protected startNewInvitation(): void {
    this.rememberCreation(null);
    this.error.set(null);
  }

  private readPendingCreation(): GameCreation | null {
    try {
      const stored = this.window.sessionStorage.getItem(pendingCreationKey);
      const attempt = JSON.parse(stored ?? 'null') as {
        login?: unknown; requestId?: unknown; opponentId?: unknown; opponentUsername?: unknown;
      } | null;
      if (this.login && attempt?.login === this.login
        && typeof attempt.requestId === 'string' && gameIdPattern.test(attempt.requestId)) {
        const { opponentId, opponentUsername, requestId } = attempt;
        if (opponentId === undefined && typeof opponentUsername === 'string'
          && opponentUsername.trim() && opponentUsername.trim().length <= maxUsernameLength) {
          return { opponentUsername, requestId };
        }
        if (opponentUsername === undefined && typeof opponentId === 'string' && gameIdPattern.test(opponentId)) {
          return { opponentId, requestId };
        }
      }
    } catch {
      // Stay usable when storage is blocked or contains invalid data.
    }
    return null;
  }

  private rememberCreation(attempt: GameCreation | null): void {
    this.pendingCreation.set(attempt);
    if (!this.login || this.session.loginKey() !== this.login) {
      return;
    }
    try {
      if (attempt) {
        this.window.sessionStorage.setItem(pendingCreationKey, JSON.stringify({ login: this.login, ...attempt }));
      } else {
        this.window.sessionStorage.removeItem(pendingCreationKey);
      }
    } catch {
      // The pending attempt remains available on this page.
    }
  }
}
