import { ParamMap } from '@angular/router';
import { gameIdPattern } from './game.models';

export type InvitationReturn = { gameId: string } | { lobbyId: string };

export function invitationReturn(params: ParamMap, gameIdParam = 'gameId'): InvitationReturn | undefined {
  const lobbyId = params.get('lobbyId');
  if (lobbyId && gameIdPattern.test(lobbyId)) {
    return { lobbyId };
  }
  const gameId = params.get(gameIdParam);
  return gameId && gameIdPattern.test(gameId) ? { gameId } : undefined;
}

export function invitationReturnPath(target: InvitationReturn | undefined): string[] {
  if (!target) {
    return ['/home'];
  }
  return 'lobbyId' in target ? ['/lobbies', target.lobbyId] : ['/games', target.gameId];
}
