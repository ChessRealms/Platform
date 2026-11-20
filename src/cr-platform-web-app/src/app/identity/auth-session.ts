import { computed, Injectable, signal } from '@angular/core';
import { LoginResult } from './identity-api.models';

interface SessionTokens {
  accessToken: string;
  refreshToken: string;
  expiresAt: number;
}

@Injectable({ providedIn: 'root' })
export class AuthSession {
  private readonly tokens = signal<SessionTokens | null>(null);
  readonly hasSession = computed(() => this.tokens() !== null);

  start(result: LoginResult): boolean {
    if (result.code !== 'Success' || result.tokenType !== 'Bearer'
      || !result.accessToken || !result.refreshToken
      || !result.expiresIn || result.expiresIn <= 0) {
      return false;
    }

    this.tokens.set({
      accessToken: result.accessToken,
      refreshToken: result.refreshToken,
      expiresAt: Date.now() + result.expiresIn * 1000,
    });
    return true;
  }

  isAuthenticated(): boolean {
    const tokens = this.tokens();
    if (tokens === null) {
      return false;
    }

    if (Date.now() >= tokens.expiresAt) {
      this.tokens.set(null);
      return false;
    }

    return true;
  }
}
