import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { of, Subject, throwError } from 'rxjs';
import { vi } from 'vitest';
import { AuthSession } from '../identity/auth-session';
import { gameIdPattern, GameListItem, GameListPage } from './game.models';
import { GamesApi } from './games-api';
import { GamesPage } from './games-page';

describe('My games', () => {
  const incoming: GameListItem = {
    id: 'a1', opponentId: 'p1', opponentName: 'Rook', color: 'Black', status: 'Pending', createdAt: null,
    isYourTurn: false, winner: null, endReason: null,
  };
  const outgoing: GameListItem = { ...incoming, id: 'a2', opponentName: 'Knight', color: 'White' };
  const opponentId = '1ae1fd73-7d50-43e2-bc5c-f9ef232fcdad';
  let fixture: ComponentFixture<GamesPage>;
  let element: HTMLElement;

  afterEach(() => sessionStorage.clear());

  function open(list: ReturnType<typeof vi.fn>, create = vi.fn()): void {
    TestBed.configureTestingModule({ providers: [
      provideRouter([]),
      { provide: AuthSession, useValue: { loginKey: () => 'current-login' } },
    ] })
      .overrideComponent(GamesPage, { set: { providers: [
        { provide: GamesApi, useValue: { list, create, me: () => of({ userId: 'p2' }) } },
      ] } });
    render();
  }

  function render(): void {
    fixture = TestBed.createComponent(GamesPage);
    element = fixture.nativeElement;
    fixture.detectChanges();
  }

  function enterOpponent(): void {
    const input = element.querySelector<HTMLInputElement>('#opponent-id')!;
    input.value = opponentId;
    input.dispatchEvent(new Event('input'));
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

  it('shows a resignation draw and its reason in the finished list', () => {
    const finished: GameListItem = { ...incoming, status: 'Finished', endReason: 'ResignationNoPossibleMate' };
    const list = vi.fn().mockReturnValueOnce(of({ items: [], nextCursor: null }))
      .mockReturnValueOnce(of({ items: [finished], nextCursor: null }));
    open(list);

    click('Finished');
    expect(element.querySelector('.game-status')?.textContent)
      .toBe('Draw · Insufficient mating material after resignation');
  });

  it('reuses the creation request after a lost response and a reload', async () => {
    const list = vi.fn().mockReturnValue(of({ items: [], nextCursor: null }));
    const create = vi.fn().mockReturnValueOnce(throwError(() => ({ status: 0 })))
      .mockReturnValueOnce(of({ id: 'saved-game' }));
    open(list, create);
    await fixture.whenStable();
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    enterOpponent();
    click('Create private game');
    const requestId: string = create.mock.calls[0][1];
    expect(requestId).toMatch(gameIdPattern);
    expect(element.textContent).toContain('Game creation hasn’t been confirmed');
    expect(navigate).not.toHaveBeenCalled();

    fixture.destroy();
    render();
    await fixture.whenStable();
    expect(element.querySelector<HTMLInputElement>('#opponent-id')?.value).toBe(opponentId);
    expect(element.querySelector<HTMLInputElement>('#opponent-id')?.readOnly).toBe(true);
    click('Retry creation');

    expect(create.mock.calls).toEqual([[opponentId, requestId], [opponentId, requestId]]);
    expect(navigate).toHaveBeenCalledWith(['/games', 'saved-game']);
    expect(sessionStorage.length).toBe(0);
  });

  it('uses a fresh request ID when the player chooses to start a new invitation', async () => {
    const list = vi.fn().mockReturnValue(of({ items: [], nextCursor: null }));
    const create = vi.fn().mockReturnValue(throwError(() => ({ status: 0 })));
    open(list, create);
    await fixture.whenStable();
    enterOpponent();
    click('Create private game');
    click('Start a new invitation');
    expect(create).toHaveBeenCalledTimes(1);
    click('Create private game');
    expect(create).toHaveBeenCalledTimes(2);
    expect(create.mock.calls[1][0]).toBe(opponentId);
    expect(create.mock.calls[1][1]).not.toBe(create.mock.calls[0][1]);
  });
});
