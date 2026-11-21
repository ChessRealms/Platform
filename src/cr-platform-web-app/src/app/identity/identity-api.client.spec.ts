import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { classifyIdentityApiError } from './identity-api-error';
import { IdentityApiClient } from './identity-api.client';
import { RegistrationResult } from './identity-api.models';

describe('IdentityApiClient', () => {
  let client: IdentityApiClient;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    client = TestBed.inject(IdentityApiClient);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('uses same-origin identity routes and supports registration without confirmation metadata', () => {
    let result: RegistrationResult | undefined;
    const registration = { email: 'player@example.test', username: 'player', password: 'Password9!' };
    client.register(registration).subscribe(value => result = value);

    const request = http.expectOne('/api/identity/register');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual(registration);
    expect(request.request.headers.has('Authorization')).toBe(false);
    request.flush({ userId: '11111111-1111-1111-1111-111111111111', errors: [] },
      { status: 201, statusText: 'Created' });
    expect(result?.emailConfirmation).toBeUndefined();
  });

  it('keeps refresh and login requests separate from bearer-authenticated requests', () => {
    client.login({ email: 'player@example.test', password: 'Password9!' }).subscribe();
    const login = http.expectOne('/api/identity/web/login');
    expect(login.request.headers.has('Authorization')).toBe(false);
    expect(login.request.headers.get('X-ChessRealms-Web')).toBe('1');
    login.flush({ code: 'Success' });

    client.refresh().subscribe();
    const refresh = http.expectOne('/api/identity/web/refresh');
    expect(refresh.request.method).toBe('POST');
    expect(refresh.request.body).toBeNull();
    expect(refresh.request.headers.has('Authorization')).toBe(false);
    expect(refresh.request.headers.get('X-ChessRealms-Web')).toBe('1');
    refresh.flush({ code: 'Success' });

    client.sessions('access-secret').subscribe();
    const sessions = http.expectOne('/api/identity/sessions');
    expect(sessions.request.headers.get('Authorization')).toBe('Bearer access-secret');
    sessions.flush([]);
  });

  it('uses the opaque confirmation token only for confirmation status and resend', () => {
    client.getConfirmationStatus('confirmation-secret').subscribe();
    const status = http.expectOne('/api/identity/email-confirmation');
    expect(status.request.headers.get('Authorization')).toBe('Bearer confirmation-secret');
    status.flush({ code: 'EmailNotConfirmed', emailConfirmation: null });

    client.resendConfirmation('confirmation-secret').subscribe();
    const resend = http.expectOne('/api/identity/email-confirmation/resend');
    expect(resend.request.method).toBe('POST');
    expect(resend.request.headers.get('Authorization')).toBe('Bearer confirmation-secret');
    resend.flush({ code: 'Queued', emailConfirmation: null });

    client.confirmEmail('user-id', 'token with spaces').subscribe();
    const confirm = http.expectOne(request => request.url === '/api/identity/confirm-email');
    expect(confirm.request.headers.has('Authorization')).toBe(false);
    expect(confirm.request.params.get('userId')).toBe('user-id');
    expect(confirm.request.params.get('token')).toBe('token with spaces');
    confirm.flush({ code: 'EmailConfirmed' });
  });

  it('maps account operations to their protected API routes', () => {
    client.me('access-secret').subscribe();
    const me = http.expectOne('/api/identity/me');
    expect(me.request.headers.get('Authorization')).toBe('Bearer access-secret');
    me.flush({ userId: 'user-id' });

    client.logout('access-secret').subscribe();
    const logout = http.expectOne('/api/identity/web/logout');
    expect(logout.request.method).toBe('POST');
    expect(logout.request.headers.get('Authorization')).toBe('Bearer access-secret');
    expect(logout.request.headers.get('X-ChessRealms-Web')).toBe('1');
    logout.flush(null, { status: 204, statusText: 'No Content' });

    client.revokeSession('access-secret', 'session-id').subscribe();
    const revoke = http.expectOne('/api/identity/sessions/session-id');
    expect(revoke.request.method).toBe('DELETE');
    expect(revoke.request.headers.get('Authorization')).toBe('Bearer access-secret');
    revoke.flush(null, { status: 204, statusText: 'No Content' });

    client.logoutAll('access-secret').subscribe();
    const logoutAll = http.expectOne('/api/identity/web/logout-all');
    expect(logoutAll.request.method).toBe('POST');
    expect(logoutAll.request.headers.get('Authorization')).toBe('Bearer access-secret');
    expect(logoutAll.request.headers.get('X-ChessRealms-Web')).toBe('1');
    logoutAll.flush(null, { status: 204, statusText: 'No Content' });
  });

  it('distinguishes a transport failure from a rejected API request', () => {
    let networkFailure;
    client.me('access-secret').subscribe({
      error: error => networkFailure = classifyIdentityApiError(error),
    });
    http.expectOne('/api/identity/me').error(new ProgressEvent('error'));
    expect(networkFailure).toEqual({ kind: 'network' });

    let httpFailure;
    client.login({ email: 'player@example.test', password: 'Password9!' }).subscribe({
      error: error => httpFailure = classifyIdentityApiError(error),
    });
    http.expectOne('/api/identity/web/login').flush({ code: 'InvalidCredentials' },
      { status: 401, statusText: 'Unauthorized' });
    expect(httpFailure).toEqual({ kind: 'http', status: 401 });
  });
});
