import { DatePipe, DOCUMENT } from '@angular/common';
import { Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { catchError, EMPTY, finalize, Subject, switchMap } from 'rxjs';
import { AuthSession } from '../identity/auth-session';
import { finishReasons, gameIdPattern, GameListItem, GameTimeControl, invitationOutcome } from './game.models';
import { GameCreation, GameCreationOptions, GamesApi } from './games-api';
import { InvitationCountdown } from './invitation-countdown';
import { QuickPlay } from './quick-play';
import { deadlineFromServer, ServerDeadline } from './server-deadline';

const gameFilters = [
  { status: 'Pending', label: 'Invitations', empty: 'No pending invitations.' },
  { status: 'Active', label: 'Active', empty: 'No active games.' },
  { status: 'Finished', label: 'Finished', empty: 'No finished games.' },
  { status: 'ClosedInvitations', label: 'Closed invitations', empty: 'No closed invitations.' },
] as const;

type GameFilter = (typeof gameFilters)[number];
type GameRow = GameListItem & { deadline: ServerDeadline | null };
const maxUsernameLength = 256;

const pendingCreationKey = 'chessrealms.pending-game-creation';
const pendingLobbyKey = 'chessrealms.pending-lobby-creation';

@Component({
  selector: 'cr-games-page',
  providers: [GamesApi],
  imports: [DatePipe, FormsModule, InvitationCountdown, QuickPlay, RouterLink],
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
  protected readonly games = signal<GameRow[]>([]);
  protected readonly nextCursor = signal<string | null>(null);
  protected readonly listLoading = signal(false);
  protected readonly listError = signal<string | null>(null);
  protected readonly username = signal<string | null>(null);
  protected readonly usernameError = signal<string | null>(null);
  protected readonly maxUsernameLength = maxUsernameLength;
  readonly error = signal<string | null>(null);
  protected readonly existingInvitationId = signal<string | null>(null);
  readonly busy = signal(false);
  protected readonly lobbyBusy = signal(false);
  protected readonly lobbyError = signal<string | null>(null);
  protected readonly pendingLobby = signal(this.readLobbyRequest());
  protected readonly pendingCreation = signal(this.readPendingCreation());
  opponentUsername = this.pendingCreation()?.opponentUsername ?? '';
  protected timeControl: GameTimeControl | '' = this.pendingCreation()?.timeControl ?? '';
  protected lobbyTimeControl: GameTimeControl | '' = this.pendingLobby()?.timeControl ?? '';

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
      const receivedAt = performance.now();
      const rows = page.items.map(item => ({
        ...item,
        deadline: item.status === 'Pending'
          ? deadlineFromServer(item.invitationExpiresAt, page.serverTime, receivedAt) : null,
      }));
      this.games.update(items => [...items, ...rows]);
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
      if (game.isLobby) {
        return 'Waiting for a player';
      }
      return game.color === 'Black' ? 'Invitation received' : 'Waiting for acceptance';
    }
    if (game.status === 'Active') {
      return game.isYourTurn ? 'Your turn' : 'Opponent’s turn';
    }
    const outcome = invitationOutcome(game.status);
    if (outcome) {
      return outcome;
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
    const attempt = pending ?? { opponentUsername, ...this.newCreation(this.timeControl) };
    this.rememberCreation(attempt);
    this.error.set(null);
    this.existingInvitationId.set(null);
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
          if (error.error?.code === 'InvitationAlreadyPending' && gameIdPattern.test(error.error.gameId)) {
            this.existingInvitationId.set(error.error.gameId);
            this.error.set('You already have a pending invitation for this player.');
          } else if (error.error?.code === 'InvitationLimitReached') {
            this.error.set('You have three pending invitations. Cancel one or wait for it to expire.');
          } else {
            this.error.set('This request ID has already been used for a different invitation. Start a new invitation.');
          }
        } else if (error.status === 429) {
          this.rememberCreation(null);
          const seconds = Number(error.headers?.get('Retry-After'));
          this.error.set(seconds > 0
            ? `You’ve sent too many invitations to this player. Try again in ${seconds} seconds.`
            : 'You’ve sent too many invitations to this player. Try again shortly.');
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

  protected createLobby(): void {
    if (this.lobbyBusy()) {
      return;
    }
    const attempt = this.pendingLobby() ?? this.newCreation(this.lobbyTimeControl);
    this.rememberLobbyRequest(attempt);
    this.lobbyError.set(null);
    this.lobbyBusy.set(true);
    this.api.createLobby(attempt).pipe(
      takeUntilDestroyed(this.destroyRef),
      finalize(() => this.lobbyBusy.set(false)),
    ).subscribe({
      next: lobby => {
        this.rememberLobbyRequest(null);
        void this.router.navigate(['/lobbies', lobby.id]);
      },
      error: error => {
        if (error.status === 409 && error.error?.code === 'InvitationLimitReached') {
          this.rememberLobbyRequest(null);
          this.lobbyError.set('You have three pending invitations. Cancel one or wait for it to expire.');
        } else if (error.status === 409 && error.error?.code === 'CreationRequestConflict') {
          this.rememberLobbyRequest(null);
          this.lobbyError.set('This request ID has already been used for a different invitation. Create a new link.');
        } else if (error.status === 400 || error.status === 403) {
          this.rememberLobbyRequest(null);
          this.lobbyError.set('Could not create an invitation with this account.');
        } else {
          this.lobbyError.set('Could not confirm creation. Retry to open the same link.');
        }
      },
    });
  }

  private newCreation(timeControl: GameTimeControl | ''): GameCreationOptions {
    const requestId = this.window.crypto.randomUUID();
    return timeControl ? { requestId, timeControl } : { requestId };
  }

  private readLobbyRequest(): GameCreationOptions | null {
    try {
      const stored = JSON.parse(this.window.sessionStorage.getItem(pendingLobbyKey) ?? 'null');
      return this.readCreationOptions(stored);
    } catch {
      return null;
    }
  }

  private rememberLobbyRequest(attempt: GameCreationOptions | null): void {
    this.pendingLobby.set(attempt);
    if (!this.login || this.session.loginKey() !== this.login) {
      return;
    }
    try {
      if (attempt) {
        this.window.sessionStorage.setItem(pendingLobbyKey, JSON.stringify({ login: this.login, ...attempt }));
      } else {
        this.window.sessionStorage.removeItem(pendingLobbyKey);
      }
    } catch {
      // The pending attempt remains available on this page.
    }
  }

  private readPendingCreation(): GameCreation | null {
    try {
      const stored = this.window.sessionStorage.getItem(pendingCreationKey);
      const attempt = JSON.parse(stored ?? 'null') as {
        login?: unknown; requestId?: unknown; opponentId?: unknown; opponentUsername?: unknown; timeControl?: unknown;
      } | null;
      const options = this.readCreationOptions(attempt);
      if (attempt && options) {
        const { opponentId, opponentUsername } = attempt;
        if (opponentId === undefined && typeof opponentUsername === 'string'
          && opponentUsername.trim() && opponentUsername.trim().length <= maxUsernameLength) {
          return { opponentUsername, ...options };
        }
        if (opponentUsername === undefined && typeof opponentId === 'string' && gameIdPattern.test(opponentId)) {
          return { opponentId, ...options };
        }
      }
    } catch {
      // Stay usable when storage is blocked or contains invalid data.
    }
    return null;
  }

  private readCreationOptions(stored: { login?: unknown; requestId?: unknown; timeControl?: unknown } | null):
    GameCreationOptions | null {
    if (!this.login || stored?.login !== this.login
      || typeof stored.requestId !== 'string' || !gameIdPattern.test(stored.requestId)) {
      return null;
    }
    const { requestId, timeControl } = stored;
    if (timeControl === 'Blitz5') {
      return { requestId, timeControl };
    }
    return timeControl === undefined ? { requestId } : null;
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
