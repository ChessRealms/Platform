import { HttpErrorResponse } from '@angular/common/http';

interface RegistrationError {
  code: string;
  description: string;
}

export function registrationMessages(error: unknown): string[] {
  if (!(error instanceof HttpErrorResponse)) {
    return ['Registration failed. Please try again.'];
  }

  if (error.status === 0) {
    return ['Cannot reach the server. Please check your connection and try again.'];
  }

  if (error.status !== 400 && error.status !== 409) {
    return ['Registration failed. Please try again.'];
  }

  const body: unknown = error.error;
  if (typeof body !== 'object' || body === null || !('errors' in body)) {
    return ['Please check your details and try again.'];
  }

  const errors: unknown = body.errors;
  if (Array.isArray(errors)) {
    const messages = errors
      .filter((item): item is RegistrationError => typeof item?.code === 'string'
        && typeof item.description === 'string')
      .map(item => item.description);
    return messages.length > 0 ? messages : ['Please check your details and try again.'];
  }

  if (typeof errors === 'object' && errors !== null) {
    const messages = Object.values(errors)
      .filter((value): value is string[] => Array.isArray(value)
        && value.every(message => typeof message === 'string'))
      .flat();
    return messages.length > 0 ? messages : ['Please check your details and try again.'];
  }

  return ['Please check your details and try again.'];
}
