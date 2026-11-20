import { HttpErrorResponse } from '@angular/common/http';

export type IdentityApiFailure =
  | { kind: 'network' }
  | { kind: 'http'; status: number }
  | { kind: 'unexpected' };

export function classifyIdentityApiError(error: unknown): IdentityApiFailure {
  if (!(error instanceof HttpErrorResponse)) {
    return { kind: 'unexpected' };
  }

  return error.status === 0 ? { kind: 'network' } : { kind: 'http', status: error.status };
}
