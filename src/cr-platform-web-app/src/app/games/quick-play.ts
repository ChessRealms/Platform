import { DOCUMENT } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { Router, RouterLink } from '@angular/router';
import { catchError, EMPTY, exhaustMap, filter, finalize, switchMap, tap, timer } from 'rxjs';
import { GamesApi } from './games-api';
import { QuickPlaySearch, QuickPlayTicket } from './quick-play.models';

@Component({
  selector: 'cr-quick-play',
  imports: [RouterLink],
  templateUrl: './quick-play.html',
  styleUrl: './quick-play.css',
})
export class QuickPlay {
  private readonly api = inject(GamesApi);
  private readonly router = inject(Router);
  private readonly window = inject(DOCUMENT).defaultView!;
  private readonly destroyRef = inject(DestroyRef);
  protected readonly ticket = signal<QuickPlayTicket | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly loaded = signal(false);
  protected readonly pendingJoin = signal<QuickPlaySearch | null>(null);
  protected readonly waiting = computed(() => this.ticket()?.status === 'Waiting');
  private readonly waitingId = computed(() => this.waiting() ? this.ticket()!.id : null);

  constructor() {
    toObservable(this.waitingId).pipe(
      switchMap(id => id ? this.follow(id) : EMPTY),
      takeUntilDestroyed(this.destroyRef),
    ).subscribe();
    this.load();
  }

  protected load(): void {
    this.busy.set(true);
    this.error.set(null);
    this.api.currentQuickPlayTicket().pipe(
      takeUntilDestroyed(this.destroyRef),
      finalize(() => this.busy.set(false)),
    ).subscribe({
      next: ticket => {
        this.ticket.set(ticket);
        this.loaded.set(true);
      },
      error: () => this.error.set('Could not load Quick Play. Try again.'),
    });
  }

  protected join(): void {
    if (this.busy() || this.waiting()) {
      return;
    }
    const search = this.pendingJoin() ?? {
      requestId: this.window.crypto.randomUUID(),
      previousTicketId: this.ticket()?.id ?? null,
    };
    this.pendingJoin.set(search);
    this.busy.set(true);
    this.error.set(null);
    this.api.joinQuickPlay(search).pipe(
      takeUntilDestroyed(this.destroyRef),
      finalize(() => this.busy.set(false)),
    ).subscribe({
      next: ticket => {
        this.pendingJoin.set(null);
        this.receive(ticket);
      },
      error: (error: HttpErrorResponse) => {
        if (error.status === 403) {
          this.pendingJoin.set(null);
          this.error.set('Confirm your email to use Quick Play.');
        } else if (error.status === 409) {
          this.pendingJoin.set(null);
          this.loaded.set(false);
          this.error.set('The search changed. Refresh Quick Play to continue.');
        } else {
          this.error.set('Could not confirm the search. Retry to check the same request.');
        }
      },
    });
  }

  protected cancel(): void {
    const id = this.waitingId();
    if (!id || this.busy()) {
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    this.api.updateQuickPlay(id, 'cancel').pipe(
      takeUntilDestroyed(this.destroyRef),
      finalize(() => this.busy.set(false)),
    ).subscribe({
      next: ticket => {
        if (this.waitingId() === id) {
          this.receive(ticket);
        }
      },
      error: () => this.error.set('Could not confirm cancellation. Checking the search…'),
    });
  }

  private follow(id: string) {
    let nextHeartbeat = 0;
    return timer(0, 2000).pipe(
      filter(() => !this.busy()),
      exhaustMap(() => {
        const renew = performance.now() >= nextHeartbeat;
        const request = renew ? this.api.updateQuickPlay(id, 'heartbeat') : this.api.quickPlayTicket(id);
        return request.pipe(
          tap(ticket => {
            if (this.waitingId() === id) {
              if (renew) {
                nextHeartbeat = performance.now() + 15000;
              }
              this.receive(ticket);
            }
          }),
          catchError(() => {
            if (this.waitingId() === id) {
              this.error.set('Could not reach Quick Play. Reconnecting…');
            }
            return EMPTY;
          }),
        );
      }),
    );
  }

  private receive(ticket: QuickPlayTicket): void {
    this.ticket.set(ticket);
    this.error.set(null);
    if (ticket.status === 'Matched' && ticket.gameId) {
      void this.router.navigate(['/games', ticket.gameId]);
    }
  }
}
