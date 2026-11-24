export interface QuickPlayTicket {
  id: string;
  status: 'Waiting' | 'Matched' | 'Cancelled' | 'Expired';
  enqueuedAt: string;
  expiresAt: string;
  gameId: string | null;
}

export interface QuickPlaySearch {
  requestId: string;
  previousTicketId: string | null;
}
