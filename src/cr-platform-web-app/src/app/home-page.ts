import { Component, effect, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { AuthSession } from './identity/auth-session';

@Component({
  template: `
    <section class="home-card">
      <h1>Welcome to ChessRealms</h1>
      @if (session.hasSession()) {
        <p>You are signed in.</p>
        @if (error(); as message) {
          <p class="form-errors" role="alert">{{ message }}</p>
        }
        <div class="auth-actions">
          <button type="button" [disabled]="busy()" (click)="logout(false)">Sign out</button>
          <button type="button" [disabled]="busy()" (click)="logout(true)">Sign out of all sessions</button>
        </div>
      } @else {
        <p role="status">Checking your session…</p>
      }
    </section>
  `,
})
export class HomePage {
  readonly session = inject(AuthSession);
  private readonly router = inject(Router);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);

  constructor() {
    effect(() => {
      const state = this.session.state();
      if (state === 'anonymous') {
        void this.router.navigate(['/login'], { replaceUrl: true });
      } else if (state === 'unavailable' || state === 'sign-in-required') {
        void this.router.navigate(['/session-unavailable'], { replaceUrl: true });
      }
    });
  }

  async logout(all: boolean): Promise<void> {
    if (this.busy()) {
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    try {
      if (await this.session.logout(all)) {
        return;
      }
    } catch {
      // The session can still be in use if the request didn't reach the server.
    } finally {
      this.busy.set(false);
    }
    this.error.set('We couldn’t complete sign-out. Please try again.');
  }
}
