import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { of, Subject } from 'rxjs';
import { vi } from 'vitest';
import { PrivateLobby } from './game.models';
import { GamesApi } from './games-api';
import { LobbyPage } from './lobby-page';

describe('LobbyPage', () => {
  afterEach(() => vi.useRealTimers());

  it('disables joining at the server deadline and stops reads after expiry is confirmed', () => {
    vi.useFakeTimers();
    const open: PrivateLobby = {
      id: '1ae1fd73-7d50-43e2-bc5c-f9ef232fcdad', status: 'Open', revision: 0, isOwner: false,
      serverTime: '2026-01-01T00:00:00Z', expiresAt: '2026-01-01T00:00:02Z',
      hostName: 'Rook', guestName: null, gameId: null, timeControl: null,
    };
    vi.setSystemTime(Date.parse(open.serverTime) - 20 * 60_000);
    const expiry = new Subject<PrivateLobby>();
    const lobby = vi.fn().mockReturnValueOnce(of(open)).mockReturnValueOnce(expiry);
    TestBed.configureTestingModule({ providers: [
      provideRouter([]),
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap({ lobbyId: open.id }) } } },
    ] }).overrideComponent(LobbyPage, { set: { providers: [{ provide: GamesApi, useValue: { lobby } }] } });
    const fixture = TestBed.createComponent(LobbyPage);
    const root: HTMLElement = fixture.nativeElement;
    fixture.detectChanges();
    vi.advanceTimersByTime(0);
    fixture.detectChanges();

    expect(root.querySelector('time')?.textContent).toBe('Expires in 0:02');
    expect(root.querySelector('button')?.disabled).toBe(false);
    vi.advanceTimersByTime(2000);
    fixture.detectChanges();
    expect(lobby).toHaveBeenCalledTimes(2);
    expect(root.querySelector('button')?.disabled).toBe(true);

    expiry.next({ ...open, status: 'Expired', revision: 1, serverTime: open.expiresAt });
    expiry.complete();
    fixture.detectChanges();
    expect(root.textContent).toContain('Invitation expired');
    vi.advanceTimersByTime(6000);
    expect(lobby).toHaveBeenCalledTimes(2);
  });
});
