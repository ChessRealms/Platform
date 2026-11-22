export type PlayerColor = 'White' | 'Black';

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
}

export interface GameBoard {
  game: Game;
  moves: string[];
  legalMoves: string[];
}

export const gameIdPattern = /^[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$/i;
