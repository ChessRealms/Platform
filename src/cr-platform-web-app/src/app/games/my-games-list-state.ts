import { computed, DestroyRef, inject, Injectable, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { catchError, EMPTY, expand, filter, finalize, map, merge, Observable, Subject, switchMap, tap, timer, toArray } from 'rxjs';
import { GameLiveUpdates } from './game-live-updates';
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
type ListPage = { items: readonly GameRow[]; nextCursor: string | null };
type ListRequest = { before: string | null; preserveWindow?: true };
const fallbackInterval = 30_000;

@Injectable()
export class MyGamesListState {
  private readonly api = inject(GamesApi);
  private readonly live = inject(GameLiveUpdates);
  private readonly destroyRef = inject(DestroyRef);
  private readonly requests = new Subject<ListRequest>();
  private readonly selection = signal<FilterOption>(filters[0]);
  private readonly page = signal<ListPage>({ items: [], nextCursor: null });
  private readonly pending = signal(false);
  private readonly requestError = signal<string | null>(null);
  private refreshPending = false;

  readonly filters = filters;
  readonly filter = this.selection.asReadonly();
  readonly items = computed(() => this.page().items);
  readonly hasMore = computed(() => !!this.page().nextCursor);
  readonly loading = this.pending.asReadonly();
  readonly error = this.requestError.asReadonly();
  private readonly nextExpiry = computed(() => this.items().reduce(
    (earliest, item) => Math.min(earliest, item.deadline?.monotonicAt ?? Infinity), Infinity));

  constructor() {
    this.requests.pipe(
      switchMap(request => {
        this.pending.set(true);
        this.requestError.set(null);
        const page = request.preserveWindow ? this.readWindow(this.items().at(-1)) : this.readPage(request.before);
        return page.pipe(
          tap(result => this.page.set(request.before ? combinePages([this.page(), result]) : result)),
          catchError(() => {
            this.requestError.set('Could not load your games. Try again.');
            return EMPTY;
          }),
          finalize(() => {
            this.pending.set(false);
            // A refresh queued during this read runs after any synchronous replacement has started.
            if (this.refreshPending) {
              queueMicrotask(() => {
                if (this.refreshPending && !this.loading() && !this.destroyRef.destroyed) {
                  this.invalidate();
                }
              });
            }
          }),
        );
      }),
      takeUntilDestroyed(this.destroyRef),
    ).subscribe();
    this.refresh();

    const expiry = toObservable(this.nextExpiry).pipe(switchMap(at => Number.isFinite(at)
      ? timer(Math.max(0, Math.ceil(at - performance.now())) + 50) : EMPTY));
    merge(this.live.watchMyGames(), expiry, timer(fallbackInterval, fallbackInterval)).pipe(
      takeUntilDestroyed(this.destroyRef),
    ).subscribe(() => this.invalidate());
  }

  selectFilter(filter: FilterOption): void {
    if (filter !== this.selection()) {
      this.selection.set(filter);
      this.refresh();
    }
  }

  refresh(): void {
    this.refreshPending = false;
    this.page.set({ items: [], nextCursor: null });
    this.requests.next({ before: null });
  }

  loadMore(): void {
    const before = this.page().nextCursor;
    if (before && !this.loading()) {
      this.requests.next({ before });
    }
  }

  private invalidate(): void {
    if (this.loading()) {
      this.refreshPending = true;
      return;
    }
    this.refreshPending = false;
    this.requests.next({ before: null, preserveWindow: true });
  }

  private readPage(before: string | null): Observable<ListPage> {
    return this.api.list(this.filter().status, before).pipe(map(page => {
      const receivedAt = performance.now();
      return {
        items: page.items.map(item => ({
          ...item,
          deadline: item.status === 'Pending'
            ? deadlineFromServer(item.invitationExpiresAt, page.serverTime, receivedAt) : null,
        })),
        nextCursor: page.nextCursor,
      };
    }));
  }

  private readWindow(boundary: GameRow | undefined): Observable<ListPage> {
    return this.readPage(null).pipe(
      expand(page => {
        const last = page.items.at(-1);
        return boundary && last && page.nextCursor && comesBefore(last, boundary) ? this.readPage(page.nextCursor) : EMPTY;
      }, 1),
      toArray(),
      filter(pages => pages.length > 0),
      map(combinePages),
    );
  }
}

function combinePages(pages: readonly ListPage[]): ListPage {
  const items = new Map<string, GameRow>();
  for (const page of pages) {
    for (const item of page.items) {
      items.set(item.id, item);
    }
  }
  return { items: [...items.values()], nextCursor: pages.at(-1)?.nextCursor ?? null };
}

function comesBefore(row: GameRow, boundary: GameRow): boolean {
  // The API orders by UTC creation time (null last), then by UUID, both descending.
  // Compare the timestamp strings to preserve PostgreSQL precision below a millisecond.
  const rowTime = row.createdAt?.replace(/(?:Z|\+00:00)$/, '') ?? '';
  const boundaryTime = boundary.createdAt?.replace(/(?:Z|\+00:00)$/, '') ?? '';
  return rowTime > boundaryTime || (rowTime === boundaryTime && row.id > boundary.id);
}
