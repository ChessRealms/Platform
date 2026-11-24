export type PlayerColor = 'White' | 'Black';
export type GameListFilter = 'Pending' | 'Active' | 'Finished' | 'ClosedInvitations';
export type GameStatus = Exclude<GameListFilter, 'ClosedInvitations'> | 'Declined' | 'Cancelled' | 'Expired';
export type GameInvitationAction = 'accept' | 'decline' | 'cancel';
export type GameRematchAction = 'offer' | 'accept';
export type DrawClaimReason = 'ThreefoldRepetition' | 'FiftyMoveRule';
export type GameFinishingAction =
  | { kind: 'resign' | 'offer' | 'accept' | 'decline' }
  | { kind: 'claim'; reason: DrawClaimReason; intendedMove?: string };

export interface Game {
  id: string;
  whitePlayerId: string;
  blackPlayerId: string;
  status: GameStatus;
  revision: number;
  fen: string;
  sideToMove: PlayerColor | null;
  winner: PlayerColor | null;
  endReason: string | null;
  drawOfferedBy: PlayerColor | null;
  invitationExpiresAt: string | null;
  rematchExpiresAt: string | null;
  rematchOfferedBy: PlayerColor | null;
  rematchGameId: string | null;
}

export interface GameListItem {
  id: string;
  opponentId: string | null;
  opponentName: string | null;
  color: PlayerColor;
  status: GameStatus;
  createdAt: string | null;
  invitationExpiresAt: string | null;
  isYourTurn: boolean;
  winner: PlayerColor | null;
  endReason: string | null;
  isLobby: boolean;
}

export interface GameListPage {
  items: GameListItem[];
  nextCursor: string | null;
}

export interface GameBoard {
  game: Game;
  moves: string[];
  legalMoves: string[];
  drawClaims: DrawClaimReason[];
}

export interface PrivateLobby {
  id: string;
  status: 'Open' | 'Joined' | 'Cancelled' | 'Expired';
  revision: number;
  expiresAt: string;
  isOwner: boolean;
  hostName: string | null;
  gameId: string | null;
}

export const gameIdPattern = /^[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$/i;

export function invitationOutcome(status: GameStatus | undefined): string | undefined {
  switch (status) {
    case 'Declined': return 'Invitation declined';
    case 'Cancelled': return 'Invitation cancelled';
    case 'Expired': return 'Invitation expired';
    default: return undefined;
  }
}

export const finishReasons: Record<string, string> = {
  Checkmate: 'Checkmate', Stalemate: 'Stalemate', DeadPosition: 'Dead position',
  FivefoldRepetition: 'Fivefold repetition', SeventyFiveMoveRule: '75-move rule',
  Resignation: 'Resignation', DrawAgreement: 'By agreement',
  ResignationNoPossibleMate: 'Insufficient mating material after resignation',
  ThreefoldRepetition: 'Threefold repetition', FiftyMoveRule: '50-move rule',
};
