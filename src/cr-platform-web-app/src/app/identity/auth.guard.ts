import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { invitationReturn } from '../games/invitation-return';
import { AuthSession } from './auth-session';

export const authGuard: CanActivateFn = async route => {
  const session = inject(AuthSession);
  const router = inject(Router);
  const state = await session.ensureSession();
  if (state === 'authenticated') {
    return true;
  }
  const queryParams = invitationReturn(route.paramMap, 'id');
  return router.createUrlTree([state === 'anonymous' ? '/login' : '/session-unavailable'], { queryParams });
};
