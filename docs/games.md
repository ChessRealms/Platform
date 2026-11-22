# Private standard-chess games

The API starts an untimed game from the standard position. The authenticated creator
plays White and invites a distinct confirmed account by its user ID. The invited
player plays Black and explicitly accepts. White can play the first move once the
game is active.

## Play from the web app

Choose **Play chess** on the home page. Ask your opponent for the player ID shown
there and create a private game. Share the game link with that player. The link
returns them to the game after sign-in or session recovery, where they can accept
the invitation. The board shows legal destinations for the selected piece and
asks for a piece when promoting.
Arrow keys move between squares. Enter or Space selects the focused square.
The board updates when the other player acts and restores the saved game on reload.
Either player can resign or offer a draw during an active game. The recipient
can accept, decline, or play a move to decline. On their turn, a player can claim
a draw on the current position or select an intended move to support the claim.
The board shows the final result and reason to both players.

Use **My games** to reopen a game without keeping its link. **Invitations**
includes both received and sent invitations. **Active** shows whose turn it is,
and **Finished** shows the result from your perspective. Use **Refresh** to check
for changes and **Load more** to browse older entries. If a creation response
is lost, check **Invitations** for the new game before submitting again.

## HTTP contract

Every route requires a bearer access token. The API uses the JWT subject as the
acting player ID. Only participants can read or act on an existing game. An unknown
game and a game belonging to other players both return `404 NotFound`.

| Route | Request body | Successful response |
| --- | --- | --- |
| `GET /api/games?status=Pending&before={cursor}` | None | `200` with a page of your games in that status. Omit `before` for the first page. |
| `POST /api/games` | `{"opponentId":"<user-id>"}` | `201` with a pending game at revision 0 and a `Location` header. |
| `GET /api/games/{id}` | None | `200` with the current game. |
| `GET /api/games/{id}/board?afterRevision={revision}` | None | `200` with the game, coordinate move history, legal moves, and `drawClaims` available on the current position for the caller's turn. `204` if the supplied revision is current. Omit the query parameter for a full board. |
| `POST /api/games/{id}/accept` | `{"expectedRevision":0}` | `200` with the active game at revision 1. |
| `POST /api/games/{id}/moves` | `{"move":"e2e4","expectedRevision":1}` | `200` with the next position and incremented revision. |
| `POST /api/games/{id}/resign` | `{"expectedRevision":1}` | `200` with the game finished and the opponent as winner. |
| `POST /api/games/{id}/draw/offer` | `{"expectedRevision":1}` | `200` with the offerer's color. |
| `POST /api/games/{id}/draw/accept` or `/draw/decline` | `{"expectedRevision":2}` | `200` with the offer cleared. Acceptance finishes the game as a draw. |
| `POST /api/games/{id}/draw/claim` | `{"expectedRevision":9,"reason":"ThreefoldRepetition"}`, optionally with `intendedMove` | `200` with the game finished as a draw. |

The response includes `id`, `whitePlayerId`, `blackPlayerId`, `status` (`Pending`,
`Active` or `Finished`), `revision`, `fen`, `sideToMove`, `winner`, `endReason`
and `drawOfferedBy`. The latter is `White`, `Black`, or `null`.
The side to move is present for active games. A finished draw has a reason and
no winner. Coordinate moves use lowercase squares and an explicit promotion
piece when required, for example `a7a8q`.

The list endpoint defaults to `Pending` and also accepts `Active` or `Finished`.
It returns up to 20 `items` and a `nextCursor` to pass as `before`, or `null` on
the last page. Items include the game ID, opponent ID and username, the
caller's color, status, creation time, turn indicator and result. Games with a
creation time appear newest first. Games created before the listing migration
have `createdAt: null` and follow the dated games. Use the board endpoint for
positions and move history.

| Status | Meaning |
| --- | --- |
| `400` | Invalid request body, two identical or empty player IDs, or an unavailable opponent. |
| `401` | Missing or invalid access token. |
| `403 Forbidden` | White tried to accept the invitation or a player tried to respond to their own draw offer. |
| `404 NotFound` | Game unknown or caller outside the game. |
| `409` | Revisions conflict (`RevisionConflict`), the game is in the wrong state, it is the other player's turn, or the action requires a different draw offer state. |
| `422 IllegalMove` | Move notation or move legality was rejected by the chess rules. |
| `422 DrawClaimUnavailable` | The current position or intended move does not qualify for the requested draw. |

On `RevisionConflict`, read the latest game before sending a new command. A
repeated move with the same revision cannot apply twice. New game requests
create a new game each time.

Draw claims use `ThreefoldRepetition` or `FiftyMoveRule`. ChessRealms.Engine
validates each claim. A valid intended move establishes eligibility and is
recorded in the claim without adding a move to the game history. Each accepted
finishing action, draw offer, or response advances the revision. The offer
survives the offerer's own move and clears when its recipient moves or the game
finishes.

## Realtime synchronization

The API exposes a SignalR WebSocket at `/api/games/live`. Each connection calls
`Subscribe(gameId)`. The API checks participation before joining the game's
group. An unknown game and a game belonging to other players are both rejected
as unavailable.

After a successful save, the API sends `GameChanged(gameId, revision)` to the
group. The board requests a snapshot using `afterRevision` and ignores
duplicate and older notifications. After each join or reconnect, it reads the
latest board to recover updates that could have been missed. An HTTP read every
15 seconds also covers interrupted connections and dropped notifications.
Commands and snapshots continue to use the HTTP contract above.

The deployment proxy must forward WebSocket upgrades and use HTTPS. The
browser sends the short-lived bearer token in the upgrade query string because
the browser WebSocket API cannot set the authorization header. Keep query values
redacted in proxy and application telemetry. The API closes the connection at
token expiry. The reconnect uses the current access token from the web session.

Game groups live in the API process. Deployments with multiple API replicas need
a shared SignalR service or backplane for notifications across replicas.

## Persistence and rules

MigrationWorker applies the Platform migrations, including the nullable draw
offer field, before the API starts. Each row stores the participants, revision, current FEN
and result alongside the engine's JSON game record. EF Core compares the
original revision when updating the row, so two writes on one revision cannot
both succeed. Rejected commands leave the revision and board unchanged.

The engine record retains the initial position and the move sequence, so
rehydration restores repetition counts. ChessRealms.Engine 1.2.0 validates the
record and restores the playable game in one replay through `FromRecordJson`.
Read requests use the persisted FEN and outcome directly. Automatic
endings follow the engine's documented behavior, including its limited
dead-position detection.

Private games use known opponent IDs and explicit acceptance. Opponent discovery
is a subsequent feature.
