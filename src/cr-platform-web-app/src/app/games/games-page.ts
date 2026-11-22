import { Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { finalize } from 'rxjs';
import { gameIdPattern } from './game.models';
import { GamesApi } from './games-api';

@Component({
  imports: [FormsModule, RouterLink],
  template: `
    <section class="home-card">
      <a routerLink="/home">Home</a>
      <h1>Play chess</h1>
      @if (userId(); as id) {
        <p>Share your player ID with your opponent.</p>
        <label for="player-id">Your player ID</label>
        <input id="player-id" readonly [value]="id" />
      }
      @if (error(); as message) {
        <p class="form-errors" role="alert">{{ message }}</p>
      }
      <form (ngSubmit)="create()">
        <div class="form-field">
          <label for="opponent-id">Opponent’s player ID</label>
          <input id="opponent-id" name="opponentId" [(ngModel)]="opponentId" required maxlength="36"
            autocomplete="off" spellcheck="false" />
        </div>
        <button type="submit" [disabled]="busy()">Create private game</button>
      </form>
    </section>
  `,
  styles: `h1 { margin-top: 0.8rem; }`,
})
export class GamesPage {
  private readonly api = inject(GamesApi);
  private readonly destroyRef = inject(DestroyRef);
  private readonly router = inject(Router);
  readonly userId = signal<string | null>(null);
  readonly error = signal<string | null>(null);
  readonly busy = signal(false);
  opponentId = '';

  constructor() {
    this.api.me().pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: user => this.userId.set(user.userId),
      error: () => this.error.set('Could not load your player ID. Reload the page to try again.'),
    });
  }

  create(): void {
    if (this.busy()) {
      return;
    }
    const id = this.opponentId.trim();
    if (!gameIdPattern.test(id) || id.toLowerCase() === this.userId()?.toLowerCase()) {
      this.error.set('Enter a different player’s ID.');
      return;
    }
    this.error.set(null);
    this.busy.set(true);
    this.api.create(id).pipe(
      takeUntilDestroyed(this.destroyRef),
      finalize(() => this.busy.set(false)),
    ).subscribe({
      next: game => void this.router.navigate(['/games', game.id]),
      error: error => this.error.set(error.status === 400
        ? 'That player is unavailable. Check the ID and ask them to confirm their email.'
        : 'Could not confirm that the game was created. Check your connection before trying again.'),
    });
  }
}
