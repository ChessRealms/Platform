import { DOCUMENT } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { finalize, takeWhile, timeout, timer } from 'rxjs';
import { ConfirmationSession } from './confirmation-session';
import { IdentityApiClient } from './identity-api.client';
import { ConfirmationMetadata, ConfirmationSessionResult } from './identity-api.models';

@Component({
  imports: [RouterLink],
  templateUrl: './check-email-page.html',
  host: { '(document:visibilitychange)': 'checkWhenVisible()' },
})
export class CheckEmailPage {
  private readonly api = inject(IdentityApiClient);
  private readonly document = inject(DOCUMENT);
  private readonly destroyRef = inject(DestroyRef);
  private readonly metadata = signal<Omit<ConfirmationMetadata, 'sessionToken'> | null>(null);
  private readonly now = signal(Date.now());
  private token: string | null = null;
  private nextCheckAt = 0;

  readonly state = signal<'pending' | 'confirmed' | 'expired'>('pending');
  readonly operation = signal<'status' | 'resend' | null>(null);
  readonly error = signal<string | null>(null);
  readonly resendSeconds = computed(() => {
    const availableAt = this.metadata()?.resendAvailableAt;
    return availableAt ? Math.max(0, Math.ceil((Date.parse(availableAt) - this.now()) / 1000)) : 0;
  });
  readonly canResend = computed(() => !!this.metadata()?.resendAvailableAt
    && this.resendSeconds() === 0 && this.operation() === null);
  readonly deliveryMessage = computed(() => {
    switch (this.metadata()?.deliveryStatus) {
      case 'Queued': return 'Your confirmation email is queued.';
      case 'Sent': return 'Check your inbox for the confirmation link.';
      case 'Failed': return 'We couldn’t send the confirmation email.';
      case 'Cancelled': return 'This email was not sent.';
      default: return 'Waiting for the status of your confirmation email.';
    }
  });

  constructor() {
    const initial = inject(ConfirmationSession).take();
    if (initial) {
      const { sessionToken, ...metadata } = initial;
      this.token = sessionToken;
      this.metadata.set(metadata);
    }

    if (!this.sessionIsActive()) {
      return;
    }

    timer(0, 1000).pipe(
      takeWhile(() => this.state() === 'pending'),
      takeUntilDestroyed(this.destroyRef),
    ).subscribe(() => {
      if (Date.now() >= this.nextCheckAt) {
        this.checkWhenVisible();
      } else {
        this.sessionIsActive();
      }
    });
  }

  checkWhenVisible(): void {
    if (this.sessionIsActive() && this.document.visibilityState !== 'hidden') {
      this.checkStatus();
    }
  }

  checkStatus(): void {
    this.request('status');
  }

  resend(): void {
    this.now.set(Date.now());
    if (this.canResend()) {
      this.request('resend');
    }
  }

  private sessionIsActive(): boolean {
    this.now.set(Date.now());
    if (this.state() !== 'pending') {
      return false;
    }

    const expiresAt = Date.parse(this.metadata()?.expiresAt ?? '');
    if (!this.token || !(expiresAt > this.now())) {
      this.finish('expired');
      return false;
    }
    return true;
  }

  private request(operation: 'status' | 'resend'): void {
    if (!this.sessionIsActive() || !this.token || this.operation()) {
      return;
    }

    this.operation.set(operation);
    if (operation === 'resend') {
      this.error.set(null);
    }
    const response = operation === 'resend'
      ? this.api.resendConfirmation(this.token)
      : this.api.getConfirmationStatus(this.token);

    response.pipe(
      timeout(10000),
      takeUntilDestroyed(this.destroyRef),
      finalize(() => {
        const interval = this.metadata()?.deliveryStatus === 'Queued' && !this.error() ? 3000 : 12000;
        this.nextCheckAt = Date.now() + interval;
        this.operation.set(null);
      }),
    ).subscribe({
      next: result => this.applyResult(result),
      error: error => {
        if (this.state() !== 'pending') {
          return;
        }
        if (error instanceof HttpErrorResponse && error.status === 401) {
          this.finish('expired');
        } else if (error instanceof HttpErrorResponse && error.status === 429
          && error.error?.code === 'RateLimited') {
          this.applyResult(error.error);
        } else {
          this.error.set(operation === 'resend'
            ? 'We couldn’t request another email. Check the status before trying again.'
            : 'We couldn’t check the status. Please try again.');
        }
      },
    });
  }

  private applyResult(result: ConfirmationSessionResult): void {
    if (!this.sessionIsActive()) {
      return;
    }
    this.error.set(null);
    if (result.code === 'EmailConfirmed') {
      this.finish('confirmed');
      return;
    }
    if (result.emailConfirmation) {
      this.metadata.set(result.emailConfirmation);
    }
  }

  private finish(state: 'confirmed' | 'expired'): void {
    this.token = null;
    this.state.set(state);
  }
}
