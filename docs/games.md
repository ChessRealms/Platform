# Private standard-chess games

The API starts an untimed game from the standard position. The authenticated creator
plays White and invites a distinct confirmed account by its user ID. The invited
player plays Black and explicitly accepts. White can play the first move once the
game is active.

## Play from the web app

Choose **Play chess** on the home page. Ask your opponent for the player ID shown
there and create a private game. Share the game link with that player. They can
sign in from the link and accept the invitation. The board shows legal
destinations for the selected piece and asks for a piece when promoting.
Arrow keys move between squares. Enter or Space selects the focused square.
The board polls for updates every 2.5 seconds and restores the saved game on reload.

## HTTP contract

Every route requires a bearer access token. The API uses the JWT subject as the
acting player ID. Only participants can read or act on an existing game. An unknown
game and a game belonging to other players both return `404 NotFound`.

| Route | Request body | Successful response |
| --- | --- | --- |
| `POST /api/games` | `{"opponentId":"<user-id>"}` | `201` with a pending game at revision 0 and a `Location` header. |
| `GET /api/games/{id}` | None | `200` with the current game. |
| `GET /api/games/{id}/board?afterRevision={revision}` | None | `200` with the game, coordinate move history and legal moves for the caller's turn. `204` if the supplied revision is current. Omit the query parameter for a full board. |
| `POST /api/games/{id}/accept` | `{"expectedRevision":0}` | `200` with the active game at revision 1. |
| `POST /api/games/{id}/moves` | `{"move":"e2e4","expectedRevision":1}` | `200` with the next position and incremented revision. |

The response includes `id`, `whitePlayerId`, `blackPlayerId`, `status` (`Pending`,
`Active` or `Finished`), `revision`, `fen`, `sideToMove`, `winner` and `endReason`.
The side to move is present for active games. A finished draw has a reason and
no winner. Coordinate moves use lowercase squares and an explicit promotion
piece when required, for example `a7a8q`.

| Status | Meaning |
| --- | --- |
| `400` | Invalid request body, two identical or empty player IDs, or an unavailable opponent. |
| `401` | Missing or invalid access token. |
| `403 Forbidden` | White tried to accept the invitation. |
| `404 NotFound` | Game unknown or caller outside the game. |
| `409` | Revisions conflict (`RevisionConflict`), game is in the wrong state, or it is the other player's turn. |
| `422 IllegalMove` | Move notation or move legality was rejected by the chess rules. |

On `RevisionConflict`, read the latest game before sending a new command. A
repeated move with the same revision cannot apply twice. New game requests
create a new game each time.

## Persistence and rules

MigrationWorker applies the `PersistentGames` migration to `PlatformDbContext`
before the API starts. Each row stores the participants, revision, current FEN
and result alongside the engine's JSON game record. EF Core compares the
original revision when updating the row, so two writes on one revision cannot
both succeed. Rejected commands leave the revision and board unchanged.

The engine record retains the initial position and the move sequence, so
rehydration restores repetition counts. ChessRealms.Engine 1.2.0 validates the
record and restores the playable game in one replay through `FromRecordJson`.
Read requests use the persisted FEN and outcome directly. Automatic
endings follow the engine's documented behavior, including its limited
dead-position detection.

Private games use known opponent IDs and explicit acceptance. Opponent discovery,
realtime notifications, and player actions such as resignation and draw claims
are subsequent features.
