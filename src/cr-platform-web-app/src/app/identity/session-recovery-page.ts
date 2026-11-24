import { Component, inject, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { InvitationReturn, invitationReturn, invitationReturnPath } from '../games/invitation-return';
import { AuthSession } from './auth-session';

@Component({
  imports: [RouterLink],
  template: `
    <section class="auth-card" aria-labelledby="session-recovery-title">
      <h1 id="session-recovery-title">Sign-in session unavailable</h1>
      @if (session.state() === 'sign-in-required') {
        <p>We couldn’t verify your session after an interrupted request. Sign in again to continue.</p>
      } @else {
        <p>We couldn’t restore your session. Check your connection and try again.</p>
      }
      <div class="auth-actions">
        @if (session.state() !== 'sign-in-required') {
          <button type="button" [disabled]="retrying()" (click)="retry()">Try again</button>
        }
        <a class="button-link" routerLink="/login" [queryParams]="returnParams">Go to sign in</a>
      </div>
    </section>
  `,
})
export class SessionRecoveryPage {
  readonly session = inject(AuthSession);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  readonly retrying = signal(false);

  protected get returnParams(): InvitationReturn | undefined {
    return invitationReturn(this.route.snapshot.queryParamMap);
  }

  async retry(): Promise<void> {
    this.retrying.set(true);
    try {
      const state = await this.session.ensureSession(true);
      const target = this.returnParams;
      if (state === 'authenticated') {
        await this.router.navigate(invitationReturnPath(target), { replaceUrl: true });
      } else if (state === 'anonymous') {
        await this.router.navigate(['/login'], { queryParams: target, replaceUrl: true });
      }
    } finally {
      this.retrying.set(false);
    }
  }
}
