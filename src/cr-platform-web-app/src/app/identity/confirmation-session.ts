import { Injectable } from '@angular/core';
import { ConfirmationMetadata } from './identity-api.models';

@Injectable({ providedIn: 'root' })
export class ConfirmationSession {
  private pending: ConfirmationMetadata | null = null;

  start(metadata: ConfirmationMetadata): void {
    this.pending = metadata;
  }

  take(): ConfirmationMetadata | null {
    const metadata = this.pending;
    this.pending = null;
    return metadata;
  }
}
