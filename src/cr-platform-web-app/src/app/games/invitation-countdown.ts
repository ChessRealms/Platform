import { Component, computed, input } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { map, timer } from 'rxjs';

@Component({
  selector: 'cr-invitation-countdown',
  template: '<time [attr.datetime]="expiresAt()">Expires in {{ remaining() }}</time>',
})
export class InvitationCountdown {
  readonly expiresAt = input.required<string>();
  private readonly now = toSignal(timer(0, 1000).pipe(map(() => Date.now())), { initialValue: Date.now() });
  protected readonly remaining = computed(() => {
    const seconds = Math.max(0, Math.ceil((Date.parse(this.expiresAt()) - this.now()) / 1000));
    return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`;
  });
}
