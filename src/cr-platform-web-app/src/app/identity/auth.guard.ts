import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthSession } from './auth-session';

export const authGuard: CanActivateFn = async () => {
  const session = inject(AuthSession);
  const router = inject(Router);
  const state = await session.ensureSession();
  if (state === 'authenticated') {
    return true;
  }
  return router.createUrlTree([state === 'anonymous' ? '/login' : '/session-unavailable']);
};
