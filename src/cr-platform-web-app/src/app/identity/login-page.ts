import { HttpErrorResponse } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { invitationReturn, invitationReturnPath } from '../games/invitation-return';
import { AuthSession } from './auth-session';
import { ConfirmationSession } from './confirmation-session';

@Component({
  imports: [ReactiveFormsModule, RouterLink],
  templateUrl: './login-page.html',
})
export class LoginPage {
  private readonly router = inject(Router);
  private readonly session = inject(AuthSession);
  private readonly confirmation = inject(ConfirmationSession);
  private readonly route = inject(ActivatedRoute);

  readonly registered = this.route.snapshot.queryParamMap.get('registered') === '1';
  readonly form = new FormGroup({
    email: new FormControl('', { nonNullable: true, validators: [Validators.required, Validators.email] }),
    password: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
  });
  readonly submitted = signal(false);
  readonly submitting = signal(false);
  readonly errorMessage = signal<string | null>(null);

  async submit(): Promise<void> {
    if (this.submitting()) {
      return;
    }

    this.submitted.set(true);
    this.errorMessage.set(null);
    this.form.controls.email.setValue(this.form.controls.email.value.trim());
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    const { email, password } = this.form.getRawValue();
    this.submitting.set(true);
    try {
      if (!await this.session.login({ email, password })) {
        this.errorMessage.set('Sign in could not be completed. Please try again.');
        return;
      }
      this.form.controls.password.reset();
      const target = invitationReturn(this.route.snapshot.queryParamMap);
      void this.router.navigate(invitationReturnPath(target), { replaceUrl: 'lobbyId' in (target ?? {}) });
    } catch (error) {
      if (error instanceof HttpErrorResponse && error.status === 403
        && error.error?.code === 'EmailNotConfirmed' && error.error.emailConfirmation?.sessionToken) {
        this.form.controls.password.reset();
        this.confirmation.start(error.error.emailConfirmation);
        void this.router.navigate(['/check-email']);
        return;
      }
      this.errorMessage.set(this.messageFor(error));
    } finally {
      this.submitting.set(false);
    }
  }

  private messageFor(error: unknown): string {
    if (!(error instanceof HttpErrorResponse)) {
      return 'Sign in failed. Please try again.';
    }

    if (error.status === 0) {
      return 'Cannot reach the server. Please check your connection and try again.';
    }

    if (error.status === 401 && error.error?.code === 'InvalidCredentials') {
      return 'Email or password is incorrect.';
    }

    if (error.status === 403 && error.error?.code === 'EmailNotConfirmed') {
      return 'Confirm your email address before signing in.';
    }

    if (error.status === 400) {
      return 'Please check your email and password.';
    }

    return 'Sign in failed. Please try again.';
  }
}
