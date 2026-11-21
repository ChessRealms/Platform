import { Location } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { finalize, timeout } from 'rxjs';
import { IdentityApiClient } from './identity-api.client';

@Component({
  imports: [ReactiveFormsModule, RouterLink],
  templateUrl: './reset-password-page.html',
})
export class ResetPasswordPage {
  private readonly api = inject(IdentityApiClient);
  private readonly destroyRef = inject(DestroyRef);
  private link: { userId: string; token: string } | null = null;

  readonly form = new FormGroup({
    password: new FormControl('', { nonNullable: true, validators: [Validators.required, Validators.maxLength(1024)] }),
    confirmation: new FormControl('', { nonNullable: true, validators: [Validators.required, Validators.maxLength(1024)] }),
  });
  readonly submitted = signal(false);
  readonly submitting = signal(false);
  readonly state = signal<'ready' | 'invalid' | 'complete'>('invalid');
  readonly errors = signal<string[]>([]);

  constructor() {
    const route = inject(ActivatedRoute);
    const location = inject(Location);
    const fragment = route.snapshot.fragment;
    if (!fragment) {
      return;
    }

    location.replaceState('/reset-password', '', location.getState());
    const params = new URLSearchParams(fragment);
    const userId = params.get('userId');
    const token = params.get('token');
    if (userId && /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(userId)
      && token && /^[a-zA-Z0-9_-]{1,2048}$/.test(token)) {
      this.link = { userId, token };
      this.state.set('ready');
    }
  }

  submit(): void {
    if (this.submitting() || !this.link) {
      return;
    }

    this.submitted.set(true);
    this.errors.set([]);
    const { password, confirmation } = this.form.getRawValue();
    if (this.form.invalid) {
      return;
    }
    if (password !== confirmation) {
      this.errors.set(['The passwords do not match.']);
      return;
    }

    this.submitting.set(true);
    this.api.resetPassword({ ...this.link, newPassword: password }).pipe(
      timeout(10000),
      takeUntilDestroyed(this.destroyRef),
      finalize(() => this.submitting.set(false)),
    ).subscribe({
      next: () => {
        this.clearLink();
        this.state.set('complete');
      },
      error: error => {
        if (error instanceof HttpErrorResponse) {
          if (error.status === 400 && error.error?.code === 'InvalidResetToken') {
            this.clearLink();
            this.state.set('invalid');
            return;
          }
          if (error.status === 400 && error.error?.code === 'InvalidPassword' && Array.isArray(error.error.errors)) {
            this.errors.set(error.error.errors.map((item: { description: string }) => item.description));
            return;
          }
          if (error.status === 429) {
            this.errors.set(['Too many attempts. Please try again in a minute.']);
            return;
          }
        }
        this.errors.set(['The reset could not be completed. Try signing in with the new password or try again.']);
      },
    });
  }

  private clearLink(): void {
    this.link = null;
    this.form.reset();
  }
}
