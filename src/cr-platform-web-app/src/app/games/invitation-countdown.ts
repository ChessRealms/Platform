import { Component, computed, input } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { map, timer } from 'rxjs';
import { ServerDeadline } from './server-deadline';

@Component({
  selector: 'cr-invitation-countdown',
  template: '<time [attr.datetime]="deadline().expiresAt">Expires in {{ remaining() }}</time>',
})
export class InvitationCountdown {
  readonly deadline = input.required<ServerDeadline>();
  private readonly now = toSignal(timer(0, 1000).pipe(map(() => performance.now())), { initialValue: performance.now() });
  protected readonly remaining = computed(() => {
    const seconds = Math.max(0, Math.ceil((this.deadline().monotonicAt - this.now()) / 1000));
    return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`;
  });
}
