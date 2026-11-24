import { Component, computed, input } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { map, timer } from 'rxjs';
import { GameClock, PlayerColor, turnRemaining } from './game.models';

@Component({
  selector: 'cr-game-clocks',
  template: `
    <div class="clocks" role="group" aria-label="5+0 chess clocks">
      @for (time of times(); track time.color) {
        <div class="clock" [class.running]="time.running" [attr.aria-current]="time.running ? 'true' : null">
          <span>{{ time.color }}</span>
          <time [attr.datetime]="'PT' + time.seconds + 'S'">{{ time.display }}</time>
        </div>
      }
    </div>
  `,
  styles: `
    .clocks {
      display: grid;
      grid-template-columns: repeat(2, minmax(0, 1fr));
      gap: 0.5rem;
      margin-block: 0.75rem;
    }
    .clock {
      display: grid;
      gap: 0.125rem;
      padding: 0.375rem 0.625rem;
      border: 1px solid currentColor;
      border-radius: 0.375rem;
    }
    .clock.running { border-width: 2px; padding: calc(0.375rem - 1px) calc(0.625rem - 1px); }
    time { font-weight: 600; font-variant-numeric: tabular-nums; }
  `,
})
export class GameClocks {
  readonly clock = input.required<GameClock>();
  readonly sideToMove = input<PlayerColor | null>(null);
  private readonly now = toSignal(timer(0, 200).pipe(map(() => performance.now())), { initialValue: performance.now() });
  private readonly anchor = computed(() => ({
    timestamp: performance.now(),
    remaining: turnRemaining(this.clock()),
  }));
  protected readonly times = computed(() => {
    const clock = this.clock();
    const anchor = this.anchor();
    const runningTime = anchor.remaining - Math.max(0, this.now() - anchor.timestamp);
    return (['White', 'Black'] as const).map(color => {
      const running = !!clock.turnDeadline && color === this.sideToMove();
      const saved = color === 'White' ? clock.whiteMilliseconds : clock.blackMilliseconds;
      const milliseconds = running ? Math.min(saved, runningTime) : saved;
      const seconds = Math.max(0, Math.ceil(milliseconds / 1000));
      return { color, running, seconds, display: `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}` };
    });
  });
}
