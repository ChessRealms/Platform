import { InjectionToken } from '@angular/core';

export const guestGames = new InjectionToken<boolean>('guest games', { providedIn: 'root', factory: () => false });
