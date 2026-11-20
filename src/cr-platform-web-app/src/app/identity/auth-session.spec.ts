import { TestBed } from '@angular/core/testing';
import { Component } from '@angular/core';
import { provideRouter, Router } from '@angular/router';
import { AuthSession } from './auth-session';
import { authGuard } from './auth.guard';

@Component({ template: '' })
class EmptyPage {}

describe('AuthSession and protected routes', () => {
  it('sends a guest to login when visiting home', async () => {
    TestBed.configureTestingModule({ providers: [provideRouter([
      { path: 'home', canActivate: [authGuard], component: EmptyPage },
      { path: 'login', component: EmptyPage },
    ])] });
    const router = TestBed.inject(Router);
    await router.navigateByUrl('/home');
    expect(router.url).toBe('/login');
  });

  it('rejects an expired in-memory access token', () => {
    TestBed.configureTestingModule({});
    const session = TestBed.inject(AuthSession);
    expect(session.start({
      code: 'Success', accessToken: 'access-secret', refreshToken: 'refresh-secret',
      tokenType: 'Bearer', expiresIn: 600, emailConfirmation: null, refreshTokenExpiresAt: null,
    })).toBe(true);
    expect(session.isAuthenticated()).toBe(true);

    const expiredAt = Date.now() + 601_000;
    const now = vi.spyOn(Date, 'now').mockReturnValue(expiredAt);
    expect(session.isAuthenticated()).toBe(false);
    expect(session.hasSession()).toBe(false);
    now.mockRestore();
  });

  it('allows an active session to enter home', async () => {
    TestBed.configureTestingModule({ providers: [provideRouter([
      { path: 'home', canActivate: [authGuard], component: EmptyPage },
      { path: 'login', component: EmptyPage },
    ])] });
    const session = TestBed.inject(AuthSession);
    session.start({
      code: 'Success', accessToken: 'access-secret', refreshToken: 'refresh-secret',
      tokenType: 'Bearer', expiresIn: 600, emailConfirmation: null, refreshTokenExpiresAt: null,
    });

    const router = TestBed.inject(Router);
    await router.navigateByUrl('/home');
    expect(router.url).toBe('/home');
  });
});
