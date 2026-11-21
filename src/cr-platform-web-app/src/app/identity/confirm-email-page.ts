import { Location } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { timeout } from 'rxjs';
import { IdentityApiClient } from './identity-api.client';

@Component({
  imports: [RouterLink],
  templateUrl: './confirm-email-page.html',
})
export class ConfirmEmailPage {
  private readonly api = inject(IdentityApiClient);
  private readonly destroyRef = inject(DestroyRef);
  private link: { userId: string; token: string } | null = null;

  readonly state = signal<'checking' | 'confirmed' | 'already-confirmed' | 'invalid' | 'error'>('checking');

  constructor() {
    const params = inject(ActivatedRoute).snapshot.queryParamMap;
    const location = inject(Location);
    const userId = params.get('userId');
    const token = params.get('token');
    if (params.keys.length > 0) {
      location.replaceState('/confirm-email', '', location.getState());
    }
    if (!userId || !token) {
      this.state.set('invalid');
      return;
    }
    this.link = { userId, token };
    this.confirm();
  }

  confirm(): void {
    if (!this.link) {
      return;
    }

    const { userId, token } = this.link;
    this.state.set('checking');
    this.api.confirmEmail(userId, token).pipe(
      timeout(10000),
      takeUntilDestroyed(this.destroyRef),
    ).subscribe({
      next: ({ code }) => {
        if (code === 'EmailConfirmed' || code === 'AlreadyConfirmed') {
          this.link = null;
          this.state.set(code === 'EmailConfirmed' ? 'confirmed' : 'already-confirmed');
        } else {
          this.state.set('error');
        }
      },
      error: error => {
        if (error instanceof HttpErrorResponse && error.status === 400) {
          this.link = null;
          this.state.set('invalid');
        } else {
          this.state.set('error');
        }
      },
    });
  }
}
