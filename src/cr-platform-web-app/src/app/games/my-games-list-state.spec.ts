import { TestBed } from '@angular/core/testing';
import { NEVER, Observable, of, Subject } from 'rxjs';
import { vi } from 'vitest';
import { GameLiveUpdates } from './game-live-updates';
import { GameListItem, GameListPage } from './game.models';
import { GamesApi } from './games-api';
import { MyGamesListState } from './my-games-list-state';

describe('MyGamesListState', () => {
  const serverTime = '2026-01-01T00:00:00Z';
  const active: GameListItem = {
    id: 'first', opponentId: 'opponent', opponentName: 'Rook', opponentIsGuest: false,
    color: 'White', status: 'Active', createdAt: null, invitationExpiresAt: null,
    isYourTurn: true, winner: null, endReason: null, isLobby: false, timeControl: null,
  };

  afterEach(() => vi.useRealTimers());

  function open(list: GamesApi['list'], updates: Observable<null> = NEVER) {
    TestBed.configureTestingModule({
      providers: [
        MyGamesListState, { provide: GamesApi, useValue: { list } },
        { provide: GameLiveUpdates, useValue: { watchMyGames: () => updates } },
      ],
    });
    return TestBed.inject(MyGamesListState);
  }

  it('refreshes the selected filter from the first page and discards a delayed next page', () => {
    const nextPage = new Subject<GameListPage>();
    const refreshed = new Subject<GameListPage>();
    const list = vi.fn<GamesApi['list']>()
      .mockReturnValueOnce(of({ items: [], nextCursor: null, serverTime }))
      .mockReturnValueOnce(of({ items: [active], nextCursor: active.id, serverTime }))
      .mockReturnValueOnce(nextPage)
      .mockReturnValueOnce(refreshed);
    const state = open(list);

    state.selectFilter(state.filters[1]);
    expect(state.items()).toHaveLength(1);
    expect(state.hasMore()).toBe(true);

    state.loadMore();
    state.loadMore();
    expect(list.mock.calls).toEqual([['Pending', null], ['Active', null], ['Active', active.id]]);
    expect(state.loading()).toBe(true);

    state.refresh();
    expect(list).toHaveBeenLastCalledWith('Active', null);
    expect(state.items()).toEqual([]);
    expect(state.hasMore()).toBe(false);
    expect(state.loading()).toBe(true);
    expect(nextPage.observed).toBe(false);
    nextPage.next({ items: [{ ...active, id: 'stale' }], nextCursor: 'stale', serverTime });
    expect(state.items()).toEqual([]);
    expect(state.hasMore()).toBe(false);

    refreshed.next({ items: [{ ...active, id: 'updated' }], nextCursor: null, serverTime });
    refreshed.complete();
    expect(state.items().map(item => item.id)).toEqual(['updated']);
    expect(state.loading()).toBe(false);
  });

  it('revalidates through the oldest loaded row when events arrive during pagination', async () => {
    const newest = { ...active, id: 'd', createdAt: serverTime };
    const second = { ...active, id: 'c', createdAt: serverTime };
    const third = { ...active, id: 'b', createdAt: serverTime };
    const oldest = { ...active, id: 'a', createdAt: null };
    const added = { ...active, id: 'e', createdAt: serverTime };
    const updates = new Subject<null>();
    const nextPage = new Subject<GameListPage>();
    const lastPage = new Subject<GameListPage>();
    const list = vi.fn<GamesApi['list']>()
      .mockReturnValueOnce(of({ items: [], nextCursor: null, serverTime }))
      .mockReturnValueOnce(of({ items: [newest, second], nextCursor: second.id, serverTime }))
      .mockReturnValueOnce(nextPage)
      .mockReturnValueOnce(of({ items: [added, newest], nextCursor: newest.id, serverTime }))
      .mockReturnValueOnce(of({ items: [second, third], nextCursor: third.id, serverTime }))
      .mockReturnValueOnce(lastPage);
    const state = open(list, updates);

    state.selectFilter(state.filters[1]);
    state.loadMore();
    updates.next(null);
    updates.next(null);
    expect(list).toHaveBeenCalledTimes(3);

    nextPage.next({ items: [third, oldest], nextCursor: oldest.id, serverTime });
    nextPage.complete();
    await Promise.resolve();
    expect(list.mock.calls.slice(3)).toEqual([['Active', null], ['Active', newest.id], ['Active', third.id]]);
    expect(state.items().map(item => item.id)).toEqual(['d', 'c', 'b', 'a']);

    lastPage.next({ items: [oldest], nextCursor: null, serverTime });
    lastPage.complete();
    expect(state.items().map(item => item.id)).toEqual(['e', 'd', 'c', 'b', 'a']);
    expect(state.hasMore()).toBe(false);
  });

  it('reloads an expired invitation using server time without a live event', () => {
    vi.useFakeTimers();
    vi.setSystemTime(Date.parse(serverTime) + 20 * 60_000);
    const list = vi.fn<GamesApi['list']>()
      .mockReturnValueOnce(of({
        items: [{ ...active, status: 'Pending', invitationExpiresAt: '2026-01-01T00:00:01Z' }],
        nextCursor: null, serverTime,
      }))
      .mockReturnValueOnce(of({ items: [], nextCursor: null, serverTime: '2026-01-01T00:00:01Z' }));
    const state = open(list);
    TestBed.tick();

    vi.advanceTimersByTime(999);
    expect(state.items()).toHaveLength(1);
    expect(list).toHaveBeenCalledTimes(1);
    vi.advanceTimersByTime(51);
    expect(list.mock.calls).toEqual([['Pending', null], ['Pending', null]]);
    expect(state.items()).toEqual([]);
  });
});
