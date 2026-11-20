import { Component, inject, signal } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { finalize } from 'rxjs';
import { IdentityApiClient } from './identity-api.client';
import { registrationMessages } from './registration-errors';

@Component({
  imports: [ReactiveFormsModule, RouterLink],
  templateUrl: './register-page.html',
})
export class RegisterPage {
  private readonly api = inject(IdentityApiClient);
  private readonly router = inject(Router);

  readonly form = new FormGroup({
    email: new FormControl('', { nonNullable: true, validators: [Validators.required, Validators.email, Validators.maxLength(256)] }),
    username: new FormControl('', { nonNullable: true, validators: [Validators.required, Validators.maxLength(256)] }),
    password: new FormControl('', { nonNullable: true, validators: [Validators.required, Validators.maxLength(1024)] }),
  });
  readonly submitted = signal(false);
  readonly submitting = signal(false);
  readonly errors = signal<string[]>([]);
  readonly needsConfirmation = signal(false);

  submit(): void {
    if (this.submitting()) {
      return;
    }

    this.submitted.set(true);
    this.errors.set([]);
    this.form.patchValue({
      email: this.form.controls.email.value.trim(),
      username: this.form.controls.username.value.trim(),
    });
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    const { email, username, password } = this.form.getRawValue();
    this.submitting.set(true);
    this.api.register({ email, username, password })
      .pipe(finalize(() => this.submitting.set(false)))
      .subscribe({
        next: result => {
          if (result.errors?.length) {
            this.errors.set(result.errors.map(error => error.description));
            return;
          }

          if (!result.userId) {
            this.errors.set(['Registration could not be completed. Please try again.']);
            return;
          }

          this.form.reset();
          if (result.emailConfirmation) {
            this.needsConfirmation.set(true);
            return;
          }

          void this.router.navigate(['/login'], { queryParams: { registered: '1' } });
        },
        error: error => this.errors.set(registrationMessages(error)),
      });
  }
}
