import { Routes } from '@angular/router';
import { authGuard } from './identity/auth.guard';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'home' },
  { path: 'register', loadComponent: () => import('./identity/register-page').then(module => module.RegisterPage) },
  { path: 'login', loadComponent: () => import('./identity/login-page').then(module => module.LoginPage) },
  { path: 'forgot-password', loadComponent: () => import('./identity/forgot-password-page').then(module => module.ForgotPasswordPage) },
  { path: 'reset-password', loadComponent: () => import('./identity/reset-password-page').then(module => module.ResetPasswordPage) },
  { path: 'check-email', loadComponent: () => import('./identity/check-email-page').then(module => module.CheckEmailPage) },
  { path: 'confirm-email', loadComponent: () => import('./identity/confirm-email-page').then(module => module.ConfirmEmailPage) },
  { path: 'session-unavailable', loadComponent: () => import('./identity/session-recovery-page').then(module => module.SessionRecoveryPage) },
  { path: 'home', canActivate: [authGuard], loadComponent: () => import('./home-page').then(module => module.HomePage) },
  { path: 'games', canActivate: [authGuard], loadComponent: () => import('./games/game-session-page').then(module => module.GameSessionPage) },
  { path: 'games/:id', canActivate: [authGuard], loadComponent: () => import('./games/game-session-page').then(module => module.GameSessionPage) },
  { path: '**', redirectTo: 'home' },
];
