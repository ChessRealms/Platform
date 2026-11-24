import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { of, throwError } from 'rxjs';
import { vi } from 'vitest';
import { GamesApi } from './games-api';
import { QuickPlay } from './quick-play';
import { QuickPlayTicket } from './quick-play.models';

describe('QuickPlay', () => {
  const matched: QuickPlayTicket = {
    id: 'old-ticket', status: 'Matched', enqueuedAt: '2026-01-01T00:00:00Z',
    expiresAt: '2026-01-01T00:00:45Z', gameId: 'matched-game',
  };

  function open(current: QuickPlayTicket | null, join = vi.fn()) {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: GamesApi, useValue: { currentQuickPlayTicket: () => of(current), joinQuickPlay: join } },
      ],
    });
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const fixture = TestBed.createComponent(QuickPlay);
    fixture.detectChanges();
    const root: HTMLElement = fixture.nativeElement;
    const click = (label: string) => {
      [...root.querySelectorAll('button')].find(button => button.textContent?.trim() === label)!.click();
      fixture.detectChanges();
    };
    return { root, navigate, click };
  }

  it('retries the same search key after an ambiguous response', () => {
    const join = vi.fn().mockReturnValueOnce(throwError(() => ({ status: 0 }))).mockReturnValueOnce(of(matched));
    const page = open(null, join);
    page.click('Find game · 5+0');
    expect(page.root.textContent).toContain('Could not confirm the search');
    page.click('Retry search');
    expect(join.mock.calls[0][0]).toEqual(join.mock.calls[1][0]);
    expect(page.navigate).toHaveBeenCalledExactlyOnceWith(['/games', 'matched-game']);
  });

  it('offers the last match on reopen and uses its ticket as the next search baseline', () => {
    const join = vi.fn().mockReturnValue(throwError(() => ({ status: 0 })));
    const page = open(matched, join);
    expect(page.navigate).not.toHaveBeenCalled();
    expect(page.root.querySelector('a')?.getAttribute('href')).toBe('/games/matched-game');
    page.click('Find game · 5+0');
    expect(join.mock.calls[0][0].previousTicketId).toBe('old-ticket');
  });
});
