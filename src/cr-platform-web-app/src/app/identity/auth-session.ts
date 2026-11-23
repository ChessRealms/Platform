import { DOCUMENT } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { computed, DestroyRef, inject, Injectable, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { firstValueFrom, fromEvent, timeout } from 'rxjs';
import { IdentityApiClient } from './identity-api.client';
import { LoginRequest, WebSessionResult } from './identity-api.models';
import { RefreshJournal } from './refresh-journal';

type SessionState = 'checking' | 'authenticated' | 'anonymous' | 'unavailable' | 'sign-in-required';
type SharedState = 'active' | 'signed-out' | 'refresh-uncertain';

const sessionKey = 'chessrealms.web-session';
const refreshKey = 'chessrealms.web-session.refresh-in-progress';
const journalMarker = 'indexeddb';
const lockName = 'chessrealms.web-session';
const requestTimeout = 10000;

@Injectable({ providedIn: 'root' })
export class AuthSession {
  private readonly api = inject(IdentityApiClient);
  private readonly journal = inject(RefreshJournal);
  private readonly document = inject(DOCUMENT);
  private readonly window = this.document.defaultView!;
  private readonly currentState = signal<SessionState>('checking');
  private access: { token: string; expiresAt: number } | null = null;
  private marker: string | null = null;
  private readonly authenticatedMarker = signal<string | null>(null);
  private refreshing: Promise<SessionState> | null = null;
  private refreshTimer: number | undefined;

  readonly state = this.currentState.asReadonly();
  readonly hasSession = computed(() => this.state() === 'authenticated');
  // Identifies the verified browser login across token rotations. It contains no credentials.
  readonly loginKey = this.authenticatedMarker.asReadonly();

  async getAccessToken(expectedLogin: string | null): Promise<string | null> {
    const state = await this.ensureSession();
    return state === 'authenticated' && expectedLogin !== null && expectedLogin === this.loginKey()
      ? this.validAccessToken() : null;
  }

  constructor() {
    const destroyRef = inject(DestroyRef);
    fromEvent<StorageEvent>(this.window, 'storage').pipe(takeUntilDestroyed(destroyRef))
      .subscribe(async event => {
        if (event.key === sessionKey || event.key === null) {
          // A storage event can arrive while this tab is finishing its previous refresh.
          await this.refreshing;
          await this.ensureSession();
        }
      });
    fromEvent(this.document, 'visibilitychange').pipe(takeUntilDestroyed(destroyRef))
      .subscribe(() => {
        if (this.document.visibilityState === 'visible') {
          void this.ensureSession();
        }
      });
    destroyRef.onDestroy(() => this.stopTimer());
  }

  ensureSession(retry = false): Promise<SessionState> {
    if (this.refreshing) {
      return this.refreshing;
    }

    try {
      this.syncSharedState();
      if (this.validAccessToken() || this.state() === 'anonymous' || this.state() === 'sign-in-required'
        || (this.state() === 'unavailable' && !retry)) {
        return Promise.resolve(this.state());
      }
    } catch {
      this.clearAccess('unavailable');
      return Promise.resolve(this.state());
    }

    this.refreshing = this.withLock(() => this.refreshWithinLock())
      .catch(() => {
        this.clearAccess('unavailable');
        return this.state();
      })
      .finally(() => { this.refreshing = null; });
    return this.refreshing;
  }

  login(request: LoginRequest): Promise<boolean> {
    return this.withLock(async () => {
      const result = await firstValueFrom(this.api.login(request).pipe(timeout(requestTimeout)));
      if (!this.accept(result)) {
        return false;
      }
      this.publish('active');
      await this.clearPendingRefresh();
      return true;
    });
  }

  logout(all: boolean): Promise<boolean> {
    const expectedMarker = this.marker;
    return this.withLock(async () => {
      this.syncSharedState();
      if (this.marker !== expectedMarker) {
        return false;
      }
      const existing = this.validAccessToken();
      if (!existing) {
        await this.refreshWithinLock();
      }
      const token = this.validAccessToken();
      if (!token) {
        return false;
      }

      const request = all ? this.api.logoutAll(token) : this.api.logout(token);
      await firstValueFrom(request.pipe(timeout(requestTimeout)));
      this.publish('signed-out');
      this.clearAccess('anonymous');
      await this.clearPendingRefresh();
      return true;
    });
  }

  private async withLock<T>(operation: () => Promise<T>): Promise<T> {
    if (!this.window.navigator.locks) {
      throw new Error('Web sessions require Web Locks in a secure browser context.');
    }
    // Check storage access before starting an operation that could change the cookie.
    this.window.localStorage.getItem(sessionKey);
    return this.window.navigator.locks.request(lockName, operation);
  }

  private async refreshWithinLock(): Promise<SessionState> {
    this.syncSharedState();
    if (this.validAccessToken() || this.state() === 'anonymous' || this.state() === 'sign-in-required') {
      return this.state();
    }
    const pending = await this.journal.isPending();
    const legacyMarker = this.window.localStorage.getItem(refreshKey);
    if (pending || (legacyMarker !== null && legacyMarker !== journalMarker)) {
      // The previous tab closed before it could establish which refresh token is current.
      this.publish('refresh-uncertain');
      this.clearAccess('sign-in-required');
      return this.state();
    }
    if (!this.window.navigator.onLine) {
      this.clearAccess('unavailable');
      return this.state();
    }

    this.clearAccess('checking');
    await this.journal.setPending(true);
    // Keep the marker for open tabs still running the previous implementation.
    this.window.localStorage.setItem(refreshKey, journalMarker);
    try {
      const result = await firstValueFrom(this.api.refresh().pipe(timeout(requestTimeout)));
      if (!this.accept(result)) {
        throw new Error('The refresh response did not contain a usable access token.');
      }
      if (!this.marker) {
        this.publish('active');
      } else {
        this.authenticatedMarker.set(this.marker);
      }
    } catch (error) {
      if (error instanceof HttpErrorResponse && error.status === 401) {
        this.publish('signed-out');
        this.clearAccess('anonymous');
      } else {
        // A lost response may have consumed the cookie's token. Leave recovery to a new login.
        this.publish('refresh-uncertain');
        this.clearAccess('sign-in-required');
        return this.state();
      }
    }
    await this.clearPendingRefresh();
    return this.state();
  }

  private async clearPendingRefresh(): Promise<void> {
    this.window.localStorage.removeItem(refreshKey);
    await this.journal.setPending(false);
  }

  private accept(result: WebSessionResult): boolean {
    if (result.code !== 'Success' || result.tokenType !== 'Bearer' || !result.accessToken
      || !result.expiresIn || !Number.isFinite(result.expiresIn) || result.expiresIn <= 0) {
      return false;
    }
    this.stopTimer();
    const lifetime = result.expiresIn * 1000;
    this.access = { token: result.accessToken, expiresAt: Date.now() + lifetime };
    this.currentState.set('authenticated');
    this.refreshTimer = this.window.setTimeout(() => {
      if (this.document.visibilityState === 'visible') {
        void this.ensureSession();
      }
    }, lifetime + 1);
    return true;
  }

  private validAccessToken(): string | null {
    return this.access && Date.now() < this.access.expiresAt ? this.access.token : null;
  }

  private syncSharedState(): void {
    const shared = this.window.localStorage.getItem(sessionKey);
    if (shared === this.marker) {
      return;
    }
    this.marker = shared;
    this.authenticatedMarker.set(null);
    if (shared?.startsWith('signed-out:')) {
      this.clearAccess('anonymous');
    } else if (shared?.startsWith('refresh-uncertain:')) {
      this.clearAccess('sign-in-required');
    } else {
      this.clearAccess('checking');
    }
  }

  private publish(state: SharedState): void {
    const marker = state + ':' + this.window.crypto.randomUUID();
    this.window.localStorage.setItem(sessionKey, marker);
    this.marker = marker;
    this.authenticatedMarker.set(state === 'active' ? marker : null);
  }

  private clearAccess(state: SessionState): void {
    this.stopTimer();
    this.access = null;
    this.currentState.set(state);
  }

  private stopTimer(): void {
    this.window.clearTimeout(this.refreshTimer);
  }
}
