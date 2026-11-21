import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { NavigationEnd, provideRouter, Router } from '@angular/router';
import { firstValueFrom, filter } from 'rxjs';
import { ConfirmationSession } from './confirmation-session';
import { RegisterPage } from './register-page';

@Component({ template: '' })
class EmptyPage {}

describe('RegisterPage', () => {
  let http: HttpTestingController;
  let page: RegisterPage;
  let router: Router;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([
          { path: 'login', component: EmptyPage },
          { path: 'check-email', component: EmptyPage },
        ]),
      ],
    });
    page = TestBed.createComponent(RegisterPage).componentInstance;
    http = TestBed.inject(HttpTestingController);
    router = TestBed.inject(Router);
  });

  afterEach(() => http.verify());

  function fillForm(): void {
    page.form.setValue({
      email: ' player@example.test ',
      username: ' player ',
      password: 'Password9!',
    });
  }

  it('registers an auto-confirmed account and opens sign in', async () => {
    fillForm();
    page.submit();
    const navigation = firstValueFrom(router.events.pipe(filter(event => event instanceof NavigationEnd)));

    const request = http.expectOne('/api/identity/register');
    expect(request.request.body).toEqual({
      email: 'player@example.test',
      username: 'player',
      password: 'Password9!',
    });
    request.flush({ userId: 'user-id', errors: [] }, { status: 201, statusText: 'Created' });

    await navigation;
    expect(router.url).toBe('/login?registered=1');
    expect(page.form.controls.password.value).toBe('');
  });

  it('shows backend duplicate and validation errors', () => {
    fillForm();
    page.submit();
    http.expectOne('/api/identity/register').flush({
      userId: null,
      errors: [{ code: 'DuplicateEmail', description: 'Email is already registered.' }],
    }, { status: 409, statusText: 'Conflict' });
    expect(page.errors()).toEqual(['Email is already registered.']);

    page.submit();
    http.expectOne('/api/identity/register').flush({
      errors: { Email: ['The Email field is required.'] },
    }, { status: 400, statusText: 'Bad Request' });
    expect(page.errors()).toEqual(['The Email field is required.']);
  });

  it('opens the waiting page with the confirmation session held in memory', async () => {
    fillForm();
    page.submit();
    const navigation = firstValueFrom(router.events.pipe(filter(event => event instanceof NavigationEnd)));
    http.expectOne('/api/identity/register').flush({
      userId: 'user-id',
      errors: [],
      emailConfirmation: {
        sessionToken: 'secret-token',
        expiresAt: '2026-12-01T00:00:00Z',
        deliveryStatus: 'Queued',
        resendAvailableAt: null,
        failureCode: null,
      },
    }, { status: 201, statusText: 'Created' });

    await navigation;
    expect(router.url).toBe('/check-email');
    expect(TestBed.inject(ConfirmationSession).take()?.sessionToken).toBe('secret-token');
    expect(page.form.controls.password.value).toBe('');
  });

  it('does not send invalid form data', () => {
    page.submit();
    expect(page.form.invalid).toBe(true);
    http.expectNone('/api/identity/register');
  });
});
