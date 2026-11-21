import { HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { finalize, timeout } from 'rxjs';
import { IdentityApiClient } from './identity-api.client';

@Component({
  imports: [ReactiveFormsModule, RouterLink],
  templateUrl: './forgot-password-page.html',
})
export class ForgotPasswordPage {
  private readonly api = inject(IdentityApiClient);
  private readonly destroyRef = inject(DestroyRef);

  readonly form = new FormGroup({
    email: new FormControl('', {
      nonNullable: true,
      validators: [Validators.required, Validators.email, Validators.maxLength(256)],
    }),
  });
  readonly submitted = signal(false);
  readonly submitting = signal(false);
  readonly sent = signal(false);
  readonly error = signal('');

  submit(): void {
    if (this.submitting() || this.sent()) {
      return;
    }

    this.submitted.set(true);
    this.error.set('');
    const email = this.form.controls.email.value.trim();
    this.form.controls.email.setValue(email);
    if (this.form.invalid) {
      return;
    }

    this.submitting.set(true);
    this.api.requestPasswordReset(email).pipe(
      timeout(10000),
      takeUntilDestroyed(this.destroyRef),
      finalize(() => this.submitting.set(false)),
    ).subscribe({
      next: () => {
        this.form.reset();
        this.sent.set(true);
      },
      error: error => this.error.set(error instanceof HttpErrorResponse && error.status === 429
        ? 'Too many requests. Please try again in a minute.'
        : 'The request could not be completed. Please try again.'),
    });
  }
}
