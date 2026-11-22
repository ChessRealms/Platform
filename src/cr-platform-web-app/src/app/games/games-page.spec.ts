import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of, Subject, throwError } from 'rxjs';
import { vi } from 'vitest';
import { GameListItem, GameListPage } from './game.models';
import { GamesApi } from './games-api';
import { GamesPage } from './games-page';

describe('My games', () => {
  const incoming: GameListItem = {
    id: 'a1', opponentId: 'p1', opponentName: 'Rook', color: 'Black', status: 'Pending', createdAt: null,
    isYourTurn: false, winner: null, endReason: null,
  };
  const outgoing: GameListItem = { ...incoming, id: 'a2', opponentName: 'Knight', color: 'White' };
  let fixture: ComponentFixture<GamesPage>;
  let element: HTMLElement;

  function open(list: ReturnType<typeof vi.fn>): void {
    TestBed.configureTestingModule({ providers: [
      provideRouter([]),
      { provide: GamesApi, useValue: { list, me: () => of({ userId: 'p2' }) } },
    ] });
    fixture = TestBed.createComponent(GamesPage);
    element = fixture.nativeElement;
    fixture.detectChanges();
  }

  function click(label: string): void {
    const button = [...element.querySelectorAll('button')].find(item => item.textContent!.trim() === label);
    expect(button).toBeDefined();
    button!.click();
    fixture.detectChanges();
  }

  it('shows both invitation directions and ignores responses from a previous filter', () => {
    const invitations = new Subject<GameListPage>();
    const active = { ...incoming, id: 'a3', opponentName: 'Bishop', status: 'Active', isYourTurn: true };
    const list = vi.fn().mockReturnValueOnce(invitations)
      .mockReturnValueOnce(of({ items: [active], nextCursor: null }));
    open(list);
    invitations.next({ items: [incoming, outgoing], nextCursor: null });
    fixture.detectChanges();

    expect(element.textContent).toContain('Invitation received');
    expect(element.textContent).toContain('Waiting for acceptance');
    expect(element.querySelector('.game-row')?.getAttribute('href')).toBe('/games/a1');

    click('Active');
    invitations.next({ items: [incoming], nextCursor: null });
    fixture.detectChanges();
    expect(list).toHaveBeenLastCalledWith('Active', null);
    expect(element.textContent).toContain('Your turn');
    expect(element.textContent).not.toContain('Invitation received');
    expect(element.querySelector('.game-row')?.getAttribute('href')).toBe('/games/a3');
  });

  it('retains the current page and cursor when loading more fails', () => {
    const list = vi.fn().mockReturnValueOnce(of({ items: [incoming], nextCursor: incoming.id }))
      .mockReturnValueOnce(throwError(() => new Error('Unavailable')))
      .mockReturnValueOnce(of({ items: [outgoing], nextCursor: null }));
    open(list);

    click('Load more');
    expect(element.querySelector('[role="alert"]')?.textContent).toContain('Could not load your games');
    expect(element.querySelectorAll('.game-row')).toHaveLength(1);

    click('Load more');
    expect(list).toHaveBeenLastCalledWith('Pending', incoming.id);
    expect(element.querySelectorAll('.game-row')).toHaveLength(2);
    expect(element.querySelector('[role="alert"]')).toBeNull();
  });
});
