import { DatePipe } from '@angular/common';
import { Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { catchError, EMPTY, finalize, Subject, switchMap } from 'rxjs';
import { finishReasons, gameIdPattern, GameListItem } from './game.models';
import { GamesApi } from './games-api';

const gameFilters = [
  { status: 'Pending', label: 'Invitations', empty: 'No pending invitations.' },
  { status: 'Active', label: 'Active', empty: 'No active games.' },
  { status: 'Finished', label: 'Finished', empty: 'No finished games.' },
] as const;

type GameFilter = (typeof gameFilters)[number];

@Component({
  imports: [DatePipe, FormsModule, RouterLink],
  templateUrl: './games-page.html',
  styleUrl: './games-page.css',
})
export class GamesPage {
  private readonly api = inject(GamesApi);
  private readonly destroyRef = inject(DestroyRef);
  private readonly router = inject(Router);
  private readonly listRequests = new Subject<string | null>();
  protected readonly filters = gameFilters;
  protected readonly filter = signal<GameFilter>(gameFilters[0]);
  protected readonly games = signal<GameListItem[]>([]);
  protected readonly nextCursor = signal<string | null>(null);
  protected readonly listLoading = signal(false);
  protected readonly listError = signal<string | null>(null);
  readonly userId = signal<string | null>(null);
  readonly error = signal<string | null>(null);
  readonly busy = signal(false);
  opponentId = '';

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
      next: user => this.userId.set(user.userId),
      error: () => this.error.set('Could not load your player ID. Reload the page to try again.'),
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
    const id = this.opponentId.trim();
    if (!gameIdPattern.test(id) || id.toLowerCase() === this.userId()?.toLowerCase()) {
      this.error.set('Enter a different player’s ID.');
      return;
    }
    this.error.set(null);
    this.busy.set(true);
    this.api.create(id).pipe(
      takeUntilDestroyed(this.destroyRef),
      finalize(() => this.busy.set(false)),
    ).subscribe({
      next: game => void this.router.navigate(['/games', game.id]),
      error: error => this.error.set(error.status === 400
        ? 'That player is unavailable. Check the ID and ask them to confirm their email.'
        : 'Could not confirm that the game was created. Check Invitations before trying again.'),
    });
  }
}
