# Private standard-chess games

The API starts a game from the standard position. The authenticated creator
plays White and invites a distinct confirmed account by username. The invited
player plays Black and explicitly accepts. White can play the first move once the
game is active.

## Play from the web app

Choose **Play chess** on the home page to open **My games**. Share the name shown
under **Your username**, or enter your opponent’s full username and choose
**Send invitation**. Your opponent can open **Invitations** in **My games** and
accept or decline from the board. While the invitation is pending, the sender
can choose **Cancel invitation**. Both views show how much remains of the
invitation's five minutes. An open list updates when a new invitation arrives.
Either player can also reopen it from its direct link
after sign-in or session recovery. The board shows legal destinations for the
selected piece and asks for a piece when promoting.
Arrow keys move between squares. Enter or Space selects the focused square.
The board updates when the other player acts and restores the saved game on reload.
Either player can resign or offer a draw during an active game. The recipient
can accept, decline, or play a move to decline. On their turn, a player can claim
a draw on the current position or select an intended move to support the claim.
The board shows the final result and reason to both players.

Open a finished game from **My games** and choose **Review game** to replay it.
Select a move or use the playback controls to step through the game. In the
replay panel, the left and right arrow keys go to the previous and next step.
**Play** advances at a fixed pace by default, with speeds of 1×, 2× and 4×.
Games with a complete timeline also offer **Original timing**. That mode
includes the pause before the first move and any time between the last move
and the result. In timed games the recorded clock balances follow the replay.

Choose **Untimed** or **5+0** when creating a username invitation or private link.
In 5+0, each player starts with five minutes and moves add no time. White's clock
starts when the invitation is accepted or the guest joins. Only the player to
move uses time. Reopening the board restores both clocks from the server.

Either player can choose **Rematch** on the finished board. The other player
can choose **Accept rematch** within five minutes of the original result. The
countdown is shared and starts when the game ends. Accepting starts a new
standard game with the same players, reversed colors, and the same time control.
In a timed rematch, White's new clock starts at acceptance. Both open boards
follow the new game. Reopening the original result later shows **Open rematch**.

Use **My games** to reopen a game without keeping its link. **Invitations**
includes both received and sent invitations. **Active** shows whose turn it is,
and **Finished** shows the result from your perspective. Both players can reopen
declined, cancelled, or expired invitations from **Closed invitations**. The
list updates automatically. Use **Refresh** to return to the first page of the
selected filter and **Load more** to browse older entries. If a creation response
is lost, use **Retry creation** to recover the same invitation. The web app
remembers the attempt in that tab across reloads during the same login. Choose
**Start a new invitation** if you want to make a separate attempt.

Game, game list, and lobby responses include `serverTime` for invitation and
rematch countdowns, including untimed games. The web app measures elapsed time
from receipt of the response using the browser's monotonic clock. The server
checks the deadline when it processes each action.

Tabs in the same browser profile share a login. Switching accounts in another
tab reloads the board and My games for the new player. Signing out clears both
views. The board's sign-in link keeps the game ID.

## Quick Play

Choose **Find game · 5+0** from **My games** to look for an opponent. Quick Play
uses one unrated queue for confirmed accounts. It selects the longest-waiting
available opponent, assigns colors at random, and opens the same game for both
players. At matching, White's clock starts with five minutes.

Keep **My games** open while searching. **Cancel search** removes an unmatched
ticket. Reloading **My games** resumes a current search or shows **Open last
match** if the game was created while the page was away. A newer search can
start from this state. Without a heartbeat the waiting ticket expires after
45 seconds.

The authenticated endpoints are scoped to the caller's own tickets:

| Route | Purpose |
| --- | --- |
| `GET /api/quick-play` | Read the latest ticket. Returns `204` before the first search. |
| `POST /api/quick-play` | Start or recover a search with `requestId` and the last observed `previousTicketId` (null before the first search). |
| `GET /api/quick-play/{ticketId}` | Read a ticket without extending its lifetime. |
| `POST /api/quick-play/{ticketId}/heartbeat` | Renew a waiting ticket for 45 seconds. |
| `POST /api/quick-play/{ticketId}/cancel` | Cancel a waiting ticket or return the match that won the race. |

Retry a search with the same request body. An existing ticket or a later
ticket for the same account can be returned when another tab already advanced
the search. Clients poll active searches every two seconds and renew presence
approximately every 15 seconds.

The queue lock serializes joins, heartbeats, and cancellations. A match creates
two `Matched` tickets referring to the same active game in one transaction.
Tickets use server order for matching. Match-created games are excluded from
personal invitation quotas.

## Invite by link

Choose **Create private link** in **My games** and use **Copy link** to share it.
The link is valid for five minutes. It appears in the sender's **Invitations**
until someone joins, the sender chooses **Cancel invitation**, or the link
expires. The sender can reopen expired or cancelled links from **Closed invitations**.

Opening the link shows a preview. A signed-out visitor can enter a display name
and choose **Play as guest**, or sign in and return to the invitation. The first
guest or confirmed player other than the sender to join takes Black. The sender
plays White and the invitation page opens the board when it detects the join.
Guest names have a **guest** label on the board and in the sender's game list.

The guest can reload the board or reopen the link from the same browser profile.
Once joined, the session keeps access to that seat after the link expires. It
also grants access to the guest's rematches and finished-game replays. A new
browser profile can use the guest's link only while the invitation is open.

The limit of three open outgoing invitations combines links and direct username
invitations. The per-opponent limit applies to direct invitations. Reading a link
or retrying its creation leaves the original deadline in place.

### Lobby API

Account requests to `/api/lobbies` use bearer authentication. The guest
preview also accepts signed-out visitors. A lobby ID is an unguessable UUID
used in the shareable URL. Treat open links as invitations that
can be forwarded. Only the sender and the player that joined can retrieve the
resulting game ID.

| Route | Request body | Successful response |
| --- | --- | --- |
| `POST /api/lobbies` | `{"requestId":"<uuid>"}` | `201` with the lobby at revision 0 and a `Location` header. Reuse the request ID to recover the same lobby with `200`. |
| `GET /api/lobbies/{id}` | None | `200` with the current lobby for a signed-in viewer. |
| `GET /api/lobbies/{id}/guest` | None | `200` with the invitation state. An open invitation includes the host name and format. A guest who already joined can recover their game ID. |
| `POST /api/lobbies/{id}/join` | `{"expectedRevision":0}` | `200` with the joined lobby and `gameId`. Accepts an account or guest session. A retry by the same player returns that game even after the original deadline. |
| `POST /api/lobbies/{id}/cancel` | `{"expectedRevision":0}` | `200` with the cancelled lobby. Only its sender can cancel. |

Lobby statuses are `Open`, `Joined`, `Cancelled`, and `Expired`. Join and
cancel lock the lobby row before checking the deadline. Once it is reached, both
actions return `410 InvitationExpired` for an open lobby. A competing action
returns `409 RevisionConflict` or `409 LobbyAlreadyJoined`. The join and the
new active game commit together.

The games list includes the sender's open and closed link invitations in their
respective filters. A link row has `isLobby: true`, a lobby ID in `id`, and a
null opponent. Once joined, the game appears under `Active` for each account
participant. For a guest game, the sender's list includes the guest's display
name and `opponentIsGuest` indicator.

### Guest sessions

The browser first posts `{ lobbyId, displayName }` to `/api/guests/session`.
The API checks that the lobby is open before creating a guest. The response
sets a `Secure`, `HttpOnly`, `SameSite=Strict` cookie. A retry with the same
cookie returns the same guest ID. The browser sends this cookie on the separate
join request, so it can retry the join after losing the response. A new session
lives for 30 days. Activity renews it at most once per day. Deleting the cookie
or using `DELETE /api/guests/session` ends that browser's access to its guest
seats.

Guest HTTP requests use the same-origin web header. Before connecting to
SignalR, the browser obtains a five-minute proof from
`GET /api/guests/session/live`. The WebSocket requires both the proof and
the guest cookie. HTTP game actions and SignalR
subscriptions check both participant ID and participant type against the seat.
Creating invitations and listing **My games** require an account.

## Connection recovery

If the connection drops during play, the board retains the last loaded position
and checks for updates when the browser comes back online. If the access token
expired while offline, use **Restore session** to return to the same game. If an
interrupted refresh requires a new sign-in, the sign-in link also returns to that
game. Reconnecting checks what the server saved before you try a move again.

## HTTP contract

Account requests use a bearer access token. Board and replay reads, moves,
finishing actions and rematches also accept the guest cookie. Only participants
can read or act on an existing game. An unknown game and a game belonging to
other players both return `404 NotFound`.

| Route | Request body | Successful response |
| --- | --- | --- |
| `GET /api/games?status=Pending&before={cursor}` | None | `200` with a page of your games in that status. Omit `before` for the first page. |
| `POST /api/games` | `{"opponentUsername":"<username>","requestId":"<optional-uuid>"}`. Existing clients can provide `opponentId` in place of `opponentUsername`. | `201` with a pending game at revision 0 and a `Location` header. `200` with the current state of the same game on replay. |
| `GET /api/games/{id}` | None | `200` with the current game. |
| `GET /api/games/{id}/board?afterRevision={revision}` | None | `200` with the game, coordinate move history, legal moves, and `drawClaims` available on the current position for the caller's turn. `204` if the supplied revision is current. Omit the query parameter for a full board. |
| `GET /api/games/{id}/replay` | None | `200` with the initial position, replay frames, and result. Only a participant in a finished game can retrieve it. |
| `POST /api/games/{id}/accept` | `{"expectedRevision":0}` | `200` with the active game at revision 1. |
| `POST /api/games/{id}/decline` | `{"expectedRevision":0}` | `200` with the invitation `Declined` at revision 1. Only the recipient can decline. |
| `POST /api/games/{id}/cancel` | `{"expectedRevision":0}` | `200` with the invitation `Cancelled` at revision 1. Only the sender can cancel. |
| `POST /api/games/{id}/moves` | `{"move":"e2e4","expectedRevision":1}` | `200` with the next position and incremented revision. |
| `POST /api/games/{id}/resign` | `{"expectedRevision":1}` | `200` with the game finished as a win for the opponent or a draw for insufficient mating material. |
| `POST /api/games/{id}/draw/offer` | `{"expectedRevision":1}` | `200` with the offerer's color. |
| `POST /api/games/{id}/draw/accept` or `/draw/decline` | `{"expectedRevision":2}` | `200` with the offer cleared. Acceptance finishes the game as a draw. |
| `POST /api/games/{id}/draw/claim` | `{"expectedRevision":9,"reason":"ThreefoldRepetition"}`, optionally with `intendedMove` | `200` with the game finished as a draw. |
| `POST /api/games/{id}/rematch/offer` | `{"expectedRevision":8}` | `200` with the finished game and the offerer's color. |
| `POST /api/games/{id}/rematch/accept` | `{"expectedRevision":9}` | `200` with the finished game and the new active game's ID in `rematchGameId`. |

The response includes `id`, `whitePlayerId`, `blackPlayerId`, `status` (`Pending`,
`Active`, `Finished`, `Declined`, `Cancelled`, or `Expired`), `revision`,
`invitationExpiresAt`, `fen`, `sideToMove`, `winner`, `endReason`, and `drawOfferedBy`.
The side to move is present for active games. Closed invitations have no winner
or chess end reason. A finished draw has a reason and no winner.
Coordinate moves use lowercase squares and an explicit promotion
piece when required, for example `a7a8q`.

Game and board responses also include `rematchExpiresAt`, `rematchOfferedBy`,
and `rematchGameId`. Only the opponent can accept an offer. The server locks
the original row before checking the deadline. At the deadline, a new offer or
an attempt to accept a pending offer returns `410 RematchExpired`. Acceptance
creates and links the new game in one transaction. A retry by the recipient returns the
same game ID even after expiry. Finished games recorded before the feature have
no recorded finish time and no rematch window.

The list endpoint defaults to `Pending` and also accepts `Active`, `Finished`, or
`ClosedInvitations`. `ClosedInvitations` includes `Declined`, `Cancelled`, and `Expired`.
It returns up to 20 `items` and a `nextCursor` to pass as `before`, or `null` on
the last page. Items include the game ID, opponent ID and username, the
caller's color, status, creation time, invitation deadline, turn indicator and result. Games with a
creation time appear newest first. Games created before the listing migration
have `createdAt: null` and follow the dated games. Use the board endpoint for
positions and move history.

| Status | Meaning |
| --- | --- |
| `400` | Invalid request body, invalid opponent selection or a self-invitation (`InvalidPlayers`), an empty or overlong username (`InvalidOpponentUsername`), an unknown or unconfirmed opponent (`OpponentUnavailable`), or an all-zero creation UUID (`InvalidRequestId`). |
| `401` | Missing or invalid access token. |
| `403 Forbidden` | The sender tried to accept or decline an invitation, the recipient tried to cancel it, or a player tried to respond to their own draw offer. |
| `404 NotFound` | Game unknown or caller outside the game. |
| `409` | Revisions conflict (`RevisionConflict`), the game is in the wrong state, it is the other player's turn, the action requires a different draw offer state, or the creator used the same request ID for another opponent (`CreationRequestConflict`). |
| `409 InvitationAlreadyPending` | The sender already has an open invitation for the opponent. The response includes its `gameId`. |
| `409 InvitationLimitReached` | The sender has three open outgoing invitations. |
| `410 InvitationExpired` | The caller's invitation action reached an open invitation at or after its deadline. |
| `422 IllegalMove` | Move notation or move legality was rejected by the chess rules. |
| `422 DrawClaimUnavailable` | The current position or intended move does not qualify for the requested draw. |
| `429 InvitationRateLimited` | Five invitations to the same opponent were created in the last five minutes. `Retry-After` gives the wait in seconds. |

On `RevisionConflict`, read the latest game before sending a new command. A
repeated move with the same revision cannot apply twice. For invitation actions,
the server evaluates the deadline after locking the game row. If acceptance and
cancellation race and the first action gets the lock before the deadline, it
commits at revision 1 and the second returns `409 RevisionConflict`.
At the deadline, a pending invitation appears
as `Expired` at the next revision, including on conditional board reads. Accepted
games and invitations already declined or cancelled retain their state.

The deadline is five minutes after creation. The migration uses the recorded
creation time for existing pending invitations and its own execution time for
legacy pending invitations without a creation timestamp. Reading or replaying
an invitation leaves its deadline in place.

Supply exactly one of `opponentUsername` and `opponentId`. Username lookup trims
leading and trailing whitespace, accepts up to 256 characters, and matches the
full name using Identity's case-insensitive normalization.

Generate a new `requestId` for each intended game. Retry an unconfirmed creation
with the same `requestId`, opponent, and time control. A username and an ID that resolve to the
same account identify the same opponent. The request ID is unique per authenticated
creator and stored with the game, so concurrent retries resolve to the same game
ID. A replay returns the game's current state and leaves the creation quota
untouched. New invitations count toward a five-minute sliding window for the
ordered pair of account IDs, even after the invitation closes. One pending
invitation per ordered pair and three pending outgoing invitations per sender
are allowed. A duplicate returns the existing invitation's ID so the sender can
reopen it. Only successful creations bind a request ID.

Draw claims use `ThreefoldRepetition` or `FiftyMoveRule`. ChessRealms.Engine
validates each claim. A valid intended move establishes eligibility and is
recorded in the claim without adding a move to the game history. Each accepted
finishing action, draw offer, or response advances the revision. The offer
survives the offerer's own move and clears when its recipient moves or the game
finishes.

Resignation awards the opponent a win unless their mating material is
insufficient. In that case the game ends with `winner: null` and
`endReason: "ResignationNoPossibleMate"`. The material check follows the intent
of [FIDE Laws of Chess, article 5.1.2](https://handbook.fide.com/chapter/E012023).
It covers a bare king, a lone knight against a king with only queens or no other
pieces, and a bishop-only attacking side when all bishops share one square
color and the defending side has no pawns or knights. It accounts for the
defender's pieces as possible blockers. Position-specific forced lines and
locked positions fall back to the ordinary resignation result if the material
check cannot establish a draw. The board and move history are preserved.

## Realtime synchronization

The API exposes a SignalR WebSocket at `/api/games/live`. The board calls
`Subscribe(gameId)`. The API checks participation before joining the game's
group. An unknown game and a game belonging to other players are both rejected
as unavailable.

After a successful save, the API sends `GameChanged(gameId, revision)` to the
group. The board requests a snapshot using `afterRevision` and ignores
duplicate and older notifications. After each join or reconnect, it reads the
latest board to recover updates that could have been missed. An HTTP read every
15 seconds also covers interrupted connections and dropped notifications.
Finished boards keep their subscription and background reads while a rematch
can still start. Closed invitations and older finished boards load their saved
state and stop background reads.
Commands and snapshots continue to use the HTTP contract above.

My games calls `SubscribeMyGames()`. The API selects the account's group from
the authenticated identity and rejects guest subscriptions. After an invitation,
game, lobby, or Quick Play match changes the list, the API sends
`MyGamesChanged` with no payload to the affected accounts. The client reads
the selected filter through HTTP on a notification, on connection or reconnect,
at the next visible invitation deadline, and every 30 seconds as a fallback.
Background reads keep the last successful list visible and follow pagination
through the oldest visible row before replacing the list.

The deployment proxy must forward WebSocket upgrades and use HTTPS. The
browser sends the short-lived bearer token in the upgrade query string because
the browser WebSocket API cannot set the authorization header. Keep query values
redacted in proxy and application telemetry. The API closes the connection at
token expiry. The reconnect uses the current access token from the web session.

Realtime groups live in the API process. Deployments with multiple API replicas need
a shared SignalR service or backplane for notifications across replicas.

## Game clocks

Creation requests for games and private links accept `timeControl: "Blitz5"`.
Omit the field or send `null` for an untimed game. Game and board responses
include `clock` for timed games. It carries the time control, each side's
balance at the start of the running turn or when last stopped, the running
turn's absolute `turnDeadline`, and `serverTime` for rendering the countdown.
The deadline is null before play starts and after the game finishes.

The API checks the server clock after locking the game row. At the exact
deadline the side to move runs out of time. The opponent wins by `Timeout` unless
the engine's mating-material check returns insufficient material for them,
which gives `TimeoutNoPossibleMate` and a draw. A command that first detects
expiry returns the saved finished state with `200`. Board and game reads or
opening the Active or Finished list also settle an elapsed clock. The finish
time is the deadline even if both players were offline and opened the game
later.

## Persistence and rules

Replay reads return `initialFen`, `viewerColor`, `timeControl`, `timingMode`,
an optional `initialClock`, a single ordered `moves` array, and `finish`.
Each move contains the coordinate move, SAN, full move number as a string,
color, FEN after the move, and optional elapsed milliseconds and clock balances.
The finish contains the winner, reason, elapsed milliseconds, and final balances.
An unfinished game returns `409 GameNotFinished` to a participant. Unknown games
and games owned by other players return `404`. The browser loads the replay once
and steps through the positions locally.

`Games.StartedAt` records activation. `GameMoveTimings` stores one row per
committed half-move with its server timestamp and optional clock balances,
keyed by game ID and one-based ply. The move and its timing commit in the same
transaction. The existing engine record supplies the initial position and
move order. `FinishedAt` supplies the result time, including the exact clock
deadline for a flag fall. A declared intended move in a draw claim contributes
no extra frame. Original timing requires all move timestamps and both
endpoints of the timeline in chronological order. Games that started before
the timings migration still support stepping and fixed-pace playback.

MigrationWorker applies the Platform migrations, including the nullable draw
offer field, before the API starts. Each row stores the participants, revision, current FEN
and result alongside the engine's JSON game record. EF Core compares the
original revision when updating the row, so two writes on one revision cannot
both succeed. Rejected commands leave the revision and board unchanged.

Board reads check participation and revision in the same SQL query. When
`afterRevision` matches, PostgreSQL omits the JSON history from the result and
the API returns `204`.

The engine record retains the initial position and the move sequence, so
rehydration restores repetition counts. ChessRealms.Engine 1.2.0 validates the
record and restores the playable game in one replay through `FromRecordJson`.
Read requests use the persisted FEN and outcome directly. Automatic
endings follow the engine's documented behavior, including its limited
dead-position detection.
