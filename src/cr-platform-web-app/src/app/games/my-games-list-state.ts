import { computed, DestroyRef, inject, Injectable, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { catchError, EMPTY, finalize, Subject, switchMap } from 'rxjs';
import { GameListItem } from './game.models';
import { GamesApi } from './games-api';
import { deadlineFromServer, ServerDeadline } from './server-deadline';

const filters = [
  { status: 'Pending', label: 'Invitations', empty: 'No pending invitations.' },
  { status: 'Active', label: 'Active', empty: 'No active games.' },
  { status: 'Finished', label: 'Finished', empty: 'No finished games.' },
  { status: 'ClosedInvitations', label: 'Closed invitations', empty: 'No closed invitations.' },
] as const;

type FilterOption = (typeof filters)[number];
type GameRow = GameListItem & { deadline: ServerDeadline | null };

@Injectable()
export class MyGamesListState {
  private readonly api = inject(GamesApi);
  private readonly destroyRef = inject(DestroyRef);
  private readonly requests = new Subject<string | null>();
  private readonly selection = signal<FilterOption>(filters[0]);
  private readonly page = signal<{ items: readonly GameRow[]; nextCursor: string | null }>({ items: [], nextCursor: null });
  private readonly pending = signal(false);
  private readonly requestError = signal<string | null>(null);

  readonly filters = filters;
  readonly filter = this.selection.asReadonly();
  readonly items = computed(() => this.page().items);
  readonly hasMore = computed(() => !!this.page().nextCursor);
  readonly loading = this.pending.asReadonly();
  readonly error = this.requestError.asReadonly();

  constructor() {
    this.requests.pipe(
      switchMap(before => {
        this.pending.set(true);
        this.requestError.set(null);
        return this.api.list(this.filter().status, before).pipe(
          catchError(() => {
            this.requestError.set('Could not load your games. Try again.');
            return EMPTY;
          }),
          finalize(() => this.pending.set(false)),
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
      this.page.update(current => ({ items: [...current.items, ...rows], nextCursor: page.nextCursor }));
    });
    this.refresh();
  }

  selectFilter(filter: FilterOption): void {
    if (filter !== this.selection()) {
      this.selection.set(filter);
      this.refresh();
    }
  }

  refresh(): void {
    this.page.set({ items: [], nextCursor: null });
    this.requests.next(null);
  }

  loadMore(): void {
    const before = this.page().nextCursor;
    if (before && !this.loading()) {
      this.requests.next(before);
    }
  }
}
