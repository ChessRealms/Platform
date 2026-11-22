import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { gameIdPattern } from '../games/game.models';
import { AuthSession } from './auth-session';

export const authGuard: CanActivateFn = async route => {
  const session = inject(AuthSession);
  const router = inject(Router);
  const state = await session.ensureSession();
  if (state === 'authenticated') {
    return true;
  }
  const gameId = route.paramMap.get('id');
  if (state === 'anonymous' && gameId && gameIdPattern.test(gameId)) {
    return router.createUrlTree(['/login'], { queryParams: { gameId } });
  }
  return router.createUrlTree([state === 'anonymous' ? '/login' : '/session-unavailable']);
};
