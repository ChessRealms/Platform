import { GameTimeControl, PlayerColor } from './game.models';

export interface ReplayClock {
  whiteMilliseconds: number;
  blackMilliseconds: number;
}

export interface ReplayMove {
  move: string;
  san: string;
  fen: string;
  moveNumber: string;
  color: PlayerColor;
  atMs: number | null;
  clock: ReplayClock | null;
}

export interface GameReplay {
  initialFen: string;
  viewerColor: PlayerColor;
  timeControl: GameTimeControl | null;
  timingMode: 'recorded' | 'unavailable';
  initialClock: ReplayClock | null;
  moves: ReplayMove[];
  finish: { winner: PlayerColor | null; endReason: string | null; atMs: number | null; clock: ReplayClock | null };
}
