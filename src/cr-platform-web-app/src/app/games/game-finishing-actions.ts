import { Component, input, linkedSignal, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DrawClaimReason, Game, GameFinishingAction, PlayerColor } from './game.models';

@Component({
  selector: 'cr-game-finishing-actions',
  imports: [FormsModule],
  templateUrl: './game-finishing-actions.html',
  styles: `
    .actions { display: flex; flex-wrap: wrap; gap: 0.4rem; }
    .actions button { padding: 0.45rem 0.65rem; }
    .panel { margin-top: 0.75rem; padding: 0.7rem; border: 1px solid #566477; border-radius: 6px; }
    .panel p { margin: 0 0 0.55rem; }
    select { width: 100%; margin-bottom: 0.7rem; padding: 0.45rem; font: inherit; color: inherit; background: #181b20; }
  `,
})
export class GameFinishingActions {
  readonly game = input.required<Game>();
  readonly color = input.required<PlayerColor>();
  readonly canAct = input.required<boolean>();
  readonly legalMoves = input.required<readonly string[]>();
  readonly drawClaims = input.required<readonly DrawClaimReason[]>();
  readonly action = output<GameFinishingAction>();

  readonly panel = linkedSignal({
    source: () => this.game().revision,
    computation: (): 'resign' | 'claim' | null => null,
  });
  readonly claimReason = signal<DrawClaimReason>('ThreefoldRepetition');
  readonly intendedMove = signal('');

  openClaim(): void {
    this.claimReason.set(this.drawClaims()[0] ?? 'ThreefoldRepetition');
    this.intendedMove.set('');
    this.panel.set('claim');
  }

  submitClaim(): void {
    const reason = this.claimReason();
    const intendedMove = this.intendedMove();
    if (this.canAct() && (intendedMove || this.drawClaims().includes(reason))) {
      this.action.emit({ kind: 'claim', reason, intendedMove: intendedMove || undefined });
      this.panel.set(null);
    }
  }
}
