import { TestBed } from '@angular/core/testing';
import { of, Subject } from 'rxjs';
import { vi } from 'vitest';
import { GameListItem, GameListPage } from './game.models';
import { GamesApi } from './games-api';
import { MyGamesListState } from './my-games-list-state';

describe('MyGamesListState', () => {
  it('refreshes the selected filter from the first page and discards a delayed next page', () => {
    const serverTime = '2026-01-01T00:00:00Z';
    const active: GameListItem = {
      id: 'first', opponentId: 'opponent', opponentName: 'Rook', opponentIsGuest: false,
      color: 'White', status: 'Active', createdAt: null, invitationExpiresAt: null,
      isYourTurn: true, winner: null, endReason: null, isLobby: false, timeControl: null,
    };
    const nextPage = new Subject<GameListPage>();
    const refreshed = new Subject<GameListPage>();
    const list = vi.fn<GamesApi['list']>()
      .mockReturnValueOnce(of({ items: [], nextCursor: null, serverTime }))
      .mockReturnValueOnce(of({ items: [active], nextCursor: active.id, serverTime }))
      .mockReturnValueOnce(nextPage)
      .mockReturnValueOnce(refreshed);
    TestBed.configureTestingModule({
      providers: [MyGamesListState, { provide: GamesApi, useValue: { list } }],
    });
    const state = TestBed.inject(MyGamesListState);

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
});
