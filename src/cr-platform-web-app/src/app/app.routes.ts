import { Routes } from '@angular/router';
import { authGuard } from './identity/auth.guard';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'home' },
  { path: 'register', loadComponent: () => import('./identity/register-page').then(module => module.RegisterPage) },
  { path: 'login', loadComponent: () => import('./identity/login-page').then(module => module.LoginPage) },
  { path: 'home', canActivate: [authGuard], loadComponent: () => import('./home-page').then(module => module.HomePage) },
  { path: '**', redirectTo: 'home' },
];
