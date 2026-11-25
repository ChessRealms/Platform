export interface ServerDeadline {
  expiresAt: string;
  monotonicAt: number;
}

export function deadlineFromServer(expiresAt: string | null, serverTime: string, receivedAt: number): ServerDeadline | null {
  if (!expiresAt) {
    return null;
  }
  return { expiresAt, monotonicAt: receivedAt + (Date.parse(expiresAt) - Date.parse(serverTime)) };
}

export function remainingUntil(deadline: ServerDeadline): number {
  return deadline.monotonicAt - performance.now();
}
