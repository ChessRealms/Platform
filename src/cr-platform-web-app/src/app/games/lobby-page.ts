import { DOCUMENT } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { catchError, EMPTY, exhaustMap, filter, finalize, merge, Subject, timer } from 'rxjs';
import { gameIdPattern, PrivateLobby } from './game.models';
import { GamesApi } from './games-api';
import { InvitationCountdown } from './invitation-countdown';

@Component({
  selector: 'cr-lobby-page',
  imports: [InvitationCountdown, RouterLink],
  providers: [GamesApi],
  templateUrl: './lobby-page.html',
})
export class LobbyPage {
  private readonly api = inject(GamesApi);
  private readonly destroyRef = inject(DestroyRef);
  private readonly document = inject(DOCUMENT);
  private readonly router = inject(Router);
  private readonly id = inject(ActivatedRoute).snapshot.paramMap.get('lobbyId') ?? '';
  private readonly refresh = new Subject<void>();
  protected readonly lobby = signal<PrivateLobby | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly unavailable = signal(false);
  protected readonly copyStatus = signal<string | null>(null);

  constructor() {
    if (!gameIdPattern.test(this.id)) {
      this.error.set('This invitation link is invalid.');
      this.unavailable.set(true);
      return;
    }

    merge(timer(0, 3000).pipe(filter(() => !this.busy())), this.refresh).pipe(
      filter(() => !this.unavailable() && (!this.lobby() || this.lobby()?.status === 'Open')),
      exhaustMap(() => this.api.lobby(this.id).pipe(catchError((error: HttpErrorResponse) => {
        if (error.status === 404 || error.error?.code === 'LobbyAlreadyJoined') {
          this.unavailable.set(true);
          this.lobby.set(null);
          this.error.set(error.status === 404 ? 'This invitation is unavailable.' : 'This invitation has already been used.');
        } else if (error.status === 401) {
          this.unavailable.set(true);
          this.lobby.set(null);
          this.error.set('Sign in to open this invitation.');
        } else {
          this.error.set('Could not load the invitation. Retrying…');
        }
        return EMPTY;
      }))),
      takeUntilDestroyed(this.destroyRef),
    ).subscribe(lobby => this.receive(lobby));
  }

  protected get link(): string {
    return new URL('/lobbies/' + this.id, this.document.baseURI).href;
  }

  protected async copy(input: HTMLInputElement): Promise<void> {
    try {
      await this.document.defaultView!.navigator.clipboard.writeText(this.link);
      this.copyStatus.set('Link copied.');
    } catch {
      input.focus();
      input.select();
      this.copyStatus.set('Link selected. Copy it to share.');
    }
  }

  protected change(action: 'join' | 'cancel'): void {
    const lobby = this.lobby();
    if (this.busy() || lobby?.status !== 'Open' || (action === 'cancel') !== lobby.isOwner) {
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    this.api.updateLobby(lobby, action).pipe(
      takeUntilDestroyed(this.destroyRef),
      finalize(() => this.busy.set(false)),
    ).subscribe({
      next: current => this.receive(current),
      error: (error: HttpErrorResponse) => {
        if (error.status === 403) {
          this.error.set('This account cannot perform that action.');
          return;
        }
        this.refresh.next();
      },
    });
  }

  private receive(current: PrivateLobby): void {
    if (current.revision < (this.lobby()?.revision ?? 0)) {
      return;
    }
    this.error.set(null);
    this.lobby.set(current);
    if (current.gameId) {
      void this.router.navigate(['/games', current.gameId], { replaceUrl: true });
    }
  }
}
