import { HttpClient, HttpHeaders, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import {
  ConfirmationResult,
  ConfirmationSessionResult,
  CurrentUser,
  LoginRequest,
  LoginSession,
  WebSessionResult,
  RegistrationRequest,
  RegistrationResult,
} from './identity-api.models';

@Injectable({ providedIn: 'root' })
export class IdentityApiClient {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = '/api/identity';
  private readonly webUrl = this.baseUrl + '/web';
  private readonly webHeaders = new HttpHeaders({ 'X-ChessRealms-Web': '1' });

  register(request: RegistrationRequest): Observable<RegistrationResult> {
    return this.http.post<RegistrationResult>(`${this.baseUrl}/register`, request);
  }

  getConfirmationStatus(sessionToken: string): Observable<ConfirmationSessionResult> {
    return this.http.get<ConfirmationSessionResult>(`${this.baseUrl}/email-confirmation`, {
      headers: this.bearer(sessionToken),
    });
  }

  resendConfirmation(sessionToken: string): Observable<ConfirmationSessionResult> {
    return this.http.post<ConfirmationSessionResult>(`${this.baseUrl}/email-confirmation/resend`, null, {
      headers: this.bearer(sessionToken),
    });
  }

  confirmEmail(userId: string, token: string): Observable<ConfirmationResult> {
    return this.http.get<ConfirmationResult>(`${this.baseUrl}/confirm-email`, {
      params: new HttpParams().set('userId', userId).set('token', token),
    });
  }

  login(request: LoginRequest): Observable<WebSessionResult> {
    return this.http.post<WebSessionResult>(`${this.webUrl}/login`, request, { headers: this.webHeaders });
  }

  refresh(): Observable<WebSessionResult> {
    return this.http.post<WebSessionResult>(`${this.webUrl}/refresh`, null, { headers: this.webHeaders });
  }

  me(accessToken: string): Observable<CurrentUser> {
    return this.http.get<CurrentUser>(`${this.baseUrl}/me`, {
      headers: this.bearer(accessToken),
    });
  }

  sessions(accessToken: string): Observable<LoginSession[]> {
    return this.http.get<LoginSession[]>(`${this.baseUrl}/sessions`, {
      headers: this.bearer(accessToken),
    });
  }

  logout(accessToken: string): Observable<void> {
    return this.http.post<void>(`${this.webUrl}/logout`, null, {
      headers: this.webHeaders.set('Authorization', `Bearer ${accessToken}`),
    });
  }

  revokeSession(accessToken: string, sessionId: string): Observable<void> {
    return this.http.delete<void>(`${this.baseUrl}/sessions/${encodeURIComponent(sessionId)}`, {
      headers: this.bearer(accessToken),
    });
  }

  logoutAll(accessToken: string): Observable<void> {
    return this.http.post<void>(`${this.webUrl}/logout-all`, null, {
      headers: this.webHeaders.set('Authorization', `Bearer ${accessToken}`),
    });
  }

  private bearer(token: string): HttpHeaders {
    return new HttpHeaders({ Authorization: `Bearer ${token}` });
  }
}
