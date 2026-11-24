import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { vi } from 'vitest';
import { GameReplay } from './game-replay.models';
import { GameReplayPage } from './game-replay-page';
import { GamesApi } from './games-api';

describe('GameReplayPage', () => {
  const replay: GameReplay = {
    initialFen: 'rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1',
    viewerColor: 'Black',
    timeControl: 'Blitz5',
    timingMode: 'recorded',
    initialClock: { whiteMilliseconds: 300000, blackMilliseconds: 300000 },
    moves: [{
      atMs: 2000, move: 'e2e4', san: 'e4', moveNumber: '1', color: 'White',
      fen: 'rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq - 0 1',
      clock: { whiteMilliseconds: 298000, blackMilliseconds: 300000 },
    }],
    finish: {
      atMs: 7000, winner: 'White', endReason: 'Resignation',
      clock: { whiteMilliseconds: 298000, blackMilliseconds: 295000 },
    },
  };

  function open(game: GameReplay) {
    const request = vi.fn(() => of(game));
    TestBed.configureTestingModule({ providers: [provideRouter([])] }).overrideComponent(GameReplayPage, {
      set: { providers: [{ provide: GamesApi, useValue: { replay: request } }] },
    });
    const fixture = TestBed.createComponent(GameReplayPage);
    fixture.componentRef.setInput('gameId', 'c7c25c2e-d235-4270-8d6a-0e8752277af0');
    fixture.detectChanges();
    const root: HTMLElement = fixture.nativeElement;
    const click = (label: string) => {
      [...root.querySelectorAll('button')].find(button => button.textContent?.trim() === label)!.click();
      fixture.detectChanges();
    };
    const status = () => root.querySelector('.status')?.textContent?.trim();
    const advance = (ms: number) => {
      vi.advanceTimersByTime(ms);
      fixture.detectChanges();
    };
    return { root, click, advance, status, request };
  }

  beforeEach(() => vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'setInterval', 'clearInterval', 'performance'] }));
  afterEach(() => vi.useRealTimers());

  it('replays the original pauses and waits for the separate finish event', () => {
    const page = open(replay);
    const timing = page.root.querySelector<HTMLSelectElement>('#replay-mode')!;
    timing.value = 'recorded';
    timing.dispatchEvent(new Event('change'));
    page.click('Play');
    page.advance(1950);
    expect(page.status()).toBe('Starting position');
    page.advance(50);
    expect(page.status()).toBe('1. e4');
    page.advance(3000);
    expect(page.root.querySelectorAll('dd')[1].textContent).toBe('4:57');
    page.click('Pause');
    page.advance(5000);
    expect(page.status()).toBe('1. e4');
    page.click('Play');
    page.advance(2000);
    expect(page.status()).toBe('White wins · Resignation');
    expect(page.request).toHaveBeenCalledTimes(1);
  });

  it('plays an archive at a fixed pace and pauses when a move is selected', () => {
    const page = open({
      ...replay, timingMode: 'unavailable', initialClock: null,
      moves: replay.moves.map(move => ({ ...move, atMs: null, clock: null })),
      finish: { ...replay.finish, atMs: null },
    });
    expect(page.root.querySelector('#replay-mode')).toBeNull();
    page.click('Play');
    page.advance(1000);
    expect(page.status()).toBe('1. e4');
    page.click('1. e4');
    page.advance(1000);
    expect(page.status()).toBe('1. e4');
    page.click('End');
    expect(page.status()).toBe('White wins · Resignation');
    expect(page.request).toHaveBeenCalledTimes(1);
  });
});
