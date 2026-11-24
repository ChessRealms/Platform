import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, DestroyRef, inject, input, OnInit, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { Subscription, timer } from 'rxjs';
import { ChessBoard } from './chess-board';
import { finishReasons, gameIdPattern } from './game.models';
import { GameReplay } from './game-replay.models';
import { GamesApi } from './games-api';

@Component({
  selector: 'cr-game-replay-page',
  providers: [GamesApi],
  imports: [ChessBoard, RouterLink],
  templateUrl: './game-replay-page.html',
  styleUrls: ['./game-page.css', './game-replay-page.css'],
})
export class GameReplayPage implements OnInit {
  readonly gameId = input.required<string>();
  protected readonly replay = signal<GameReplay | null>(null);
  protected readonly error = signal<number | null>(null);
  protected readonly step = signal(0);
  protected readonly playing = signal(false);
  protected readonly speed = signal(1);
  protected readonly mode = signal<'review' | 'recorded'>('review');
  protected readonly emptyTargets = new Set<string>();
  private readonly api = inject(GamesApi);
  private readonly destroyRef = inject(DestroyRef);
  private readonly elapsed = signal(0);
  private playback?: Subscription;
  private readonly times = computed(() => {
    const replay = this.replay();
    if (!replay) {
      return [0];
    }
    if (this.mode() === 'recorded' && replay.timingMode === 'recorded') {
      return [0, ...replay.moves.map(move => move.atMs!), replay.finish.atMs!];
    }
    return Array.from({ length: replay.moves.length + 2 }, (_, index) => index * 1000);
  });
  protected readonly lastStep = computed(() => this.times().length - 1);
  protected readonly finished = computed(() => this.step() === this.lastStep());
  protected readonly move = computed(() => {
    const moves = this.replay()?.moves ?? [];
    return moves[Math.min(this.step(), moves.length) - 1];
  });
  protected readonly highlight = computed(() => {
    const move = this.move()?.move;
    return move ? [move.slice(0, 2), move.slice(2, 4)] : [];
  });
  protected readonly result = computed(() => {
    const finish = this.replay()?.finish;
    if (!finish) {
      return '';
    }
    const result = finish.winner ? `${finish.winner} wins` : 'Draw';
    const reason = finish.endReason && finishReasons[finish.endReason];
    return reason ? `${result} · ${reason}` : result;
  });
  protected readonly clocks = computed(() => {
    const replay = this.replay();
    if (replay?.timingMode !== 'recorded') {
      return null;
    }
    const move = this.move();
    if (this.finished()) {
      return replay.finish.clock;
    }
    const clock = move ? move.clock : replay.initialClock;
    if (!clock || this.mode() !== 'recorded') {
      return clock;
    }
    const elapsed = this.elapsed() - this.times()[this.step()];
    const whiteToMove = (move?.fen ?? replay.initialFen).split(' ')[1] === 'w';
    return {
      whiteMilliseconds: whiteToMove ? clock.whiteMilliseconds - elapsed : clock.whiteMilliseconds,
      blackMilliseconds: whiteToMove ? clock.blackMilliseconds : clock.blackMilliseconds - elapsed,
    };
  });

  ngOnInit(): void {
    this.load();
  }

  protected load(): void {
    this.error.set(null);
    if (!gameIdPattern.test(this.gameId())) {
      this.error.set(404);
      return;
    }
    this.api.replay(this.gameId()).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: replay => this.replay.set(replay),
      error: (error: HttpErrorResponse) => this.error.set(error.status),
    });
  }

  protected seek(step: number): void {
    this.pause();
    const next = Math.max(0, Math.min(this.lastStep(), step));
    this.step.set(next);
    this.elapsed.set(this.times()[next]);
  }

  protected setMode(mode: 'review' | 'recorded'): void {
    this.mode.set(mode);
    this.seek(this.step());
  }

  protected setSpeed(speed: number): void {
    const wasPlaying = this.playing();
    this.pause();
    this.speed.set(speed);
    if (wasPlaying) {
      this.play();
    }
  }

  protected togglePlayback(): void {
    if (this.playing()) {
      this.pause();
    } else {
      if (this.finished()) {
        this.seek(0);
      }
      this.play();
    }
  }

  protected useArrows(event: KeyboardEvent): void {
    if (event.target instanceof HTMLSelectElement) {
      return;
    }
    if (event.key === 'ArrowLeft' || event.key === 'ArrowRight') {
      event.preventDefault();
      this.seek(this.step() + (event.key === 'ArrowLeft' ? -1 : 1));
    }
  }

  protected formatClock(milliseconds: number): string {
    const seconds = Math.max(0, Math.ceil(milliseconds / 1000));
    return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`;
  }

  private play(): void {
    this.playing.set(true);
    const started = performance.now();
    const from = this.elapsed();
    const times = this.times();
    const speed = this.speed();
    this.playback = timer(0, 50).pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => {
      const elapsed = Math.min(times[this.lastStep()], from + (performance.now() - started) * speed);
      this.elapsed.set(elapsed);
      let step = this.step();
      while (step < this.lastStep() && times[step + 1] <= elapsed) {
        step++;
      }
      this.step.set(step);
      if (this.finished()) {
        this.pause();
      }
    });
  }

  private pause(): void {
    this.playback?.unsubscribe();
    this.playing.set(false);
  }
}
