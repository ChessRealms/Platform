import { Location } from '@angular/common';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { ConfirmEmailPage } from './confirm-email-page';

describe('ConfirmEmailPage', () => {
  const apiUrl = '/api/identity/confirm-email';
  let http: HttpTestingController;

  function open(params: Record<string, string>): ConfirmEmailPage {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap(params) } } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    TestBed.inject(Location).go('/confirm-email', 'userId=account&token=secret-link');
    return TestBed.createComponent(ConfirmEmailPage).componentInstance;
  }

  afterEach(() => http.verify());

  it.each([
    ['EmailConfirmed', 'confirmed'],
    ['AlreadyConfirmed', 'already-confirmed'],
  ])('handles %s and removes the token from the URL', (code, state) => {
    const page = open({ userId: 'account', token: 'secret-link' });
    const request = http.expectOne(req => req.url === apiUrl);
    expect(request.request.params.get('userId')).toBe('account');
    expect(request.request.params.get('token')).toBe('secret-link');
    expect(TestBed.inject(Location).path()).toBe('/confirm-email');
    request.flush({ code });
    expect(page.state()).toBe(state);
  });

  it('handles an incomplete link without calling the API', () => {
    const page = open({ userId: 'account' });
    http.expectNone(req => req.url === apiUrl);
    expect(page.state()).toBe('invalid');
  });

  it('lets the user retry a failed request and then handles an invalid link', () => {
    const page = open({ userId: 'account', token: 'secret-link' });
    http.expectOne(req => req.url === apiUrl).error(new ProgressEvent('error'));
    expect(page.state()).toBe('error');

    page.confirm();
    http.expectOne(req => req.url === apiUrl).flush({ code: 'InvalidConfirmationToken' },
      { status: 400, statusText: 'Bad Request' });
    expect(page.state()).toBe('invalid');
  });
});
