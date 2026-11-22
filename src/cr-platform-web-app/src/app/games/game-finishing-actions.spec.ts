import { TestBed } from '@angular/core/testing';
import { GameFinishingActions } from './game-finishing-actions';
import { Game, GameFinishingAction } from './game.models';

describe('GameFinishingActions', () => {
  const game: Game = {
    id: 'game', whitePlayerId: 'white', blackPlayerId: 'black', status: 'Active', revision: 8, fen: '',
    sideToMove: 'White', winner: null, endReason: null, drawOfferedBy: null,
  };

  async function open() {
    const fixture = TestBed.createComponent(GameFinishingActions);
    fixture.componentRef.setInput('game', game);
    fixture.componentRef.setInput('color', 'White');
    fixture.componentRef.setInput('canAct', true);
    fixture.componentRef.setInput('legalMoves', ['f3g1']);
    fixture.componentRef.setInput('drawClaims', []);
    const actions: GameFinishingAction[] = [];
    fixture.componentInstance.action.subscribe(action => actions.push(action));
    await fixture.whenStable();
    const element: HTMLElement = fixture.nativeElement;
    const button = (name: string) => Array.from(element.querySelectorAll('button'))
      .find(candidate => candidate.textContent?.trim() === name);
    return { fixture, actions, element, button };
  }

  it('asks for confirmation before resigning and clears it when the revision changes', async () => {
    const { fixture, actions, button } = await open();
    button('Resign')!.click();
    await fixture.whenStable();
    expect(actions).toEqual([]);
    expect(button('Confirm resignation')).toBeDefined();

    fixture.componentRef.setInput('game', { ...game, revision: 9 });
    await fixture.whenStable();
    expect(button('Confirm resignation')).toBeUndefined();

    button('Resign')!.click();
    await fixture.whenStable();
    button('Confirm resignation')!.click();
    expect(actions).toEqual([{ kind: 'resign' }]);
  });

  it('allows a claim on an intended move when the current position does not qualify', async () => {
    const { fixture, actions, element, button } = await open();
    button('Claim draw')!.click();
    await fixture.whenStable();
    expect(button('Submit claim')!.disabled).toBe(true);

    const move = element.querySelector<HTMLSelectElement>('#draw-claim-move')!;
    move.value = 'f3g1';
    move.dispatchEvent(new Event('change', { bubbles: true }));
    await fixture.whenStable();
    expect(button('Submit claim')!.disabled).toBe(false);
    button('Submit claim')!.click();
    expect(actions).toEqual([{ kind: 'claim', reason: 'ThreefoldRepetition', intendedMove: 'f3g1' }]);
  });
});
