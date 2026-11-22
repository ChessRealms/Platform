export type PlayerColor = 'White' | 'Black';
export type DrawClaimReason = 'ThreefoldRepetition' | 'FiftyMoveRule';
export type GameFinishingAction =
  | { kind: 'resign' | 'offer' | 'accept' | 'decline' }
  | { kind: 'claim'; reason: DrawClaimReason; intendedMove?: string };

export interface Game {
  id: string;
  whitePlayerId: string;
  blackPlayerId: string;
  status: 'Pending' | 'Active' | 'Finished';
  revision: number;
  fen: string;
  sideToMove: PlayerColor | null;
  winner: PlayerColor | null;
  endReason: string | null;
  drawOfferedBy: PlayerColor | null;
}

export interface GameBoard {
  game: Game;
  moves: string[];
  legalMoves: string[];
  drawClaims: DrawClaimReason[];
}

export const gameIdPattern = /^[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$/i;
