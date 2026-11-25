using ChessRealms.Platform.Application.Games;
using ChessRealms.Platform.Domain.Games;
using ChessRealms.Platform.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;

namespace ChessRealms.Platform.Infrastructure.Games;

internal sealed class PostgresGameStore(PlatformDbContext database, TimeProvider clock) : IGameStore
{
    private const int MaxPairInvitations = 5;
    private static readonly TimeSpan PairWindow = TimeSpan.FromMinutes(5);

    // Compare the revision and project the history in the same database snapshot.
    private static readonly Func<PlatformDbContext, Guid, Guid, GameParticipantKind, long?, DateTimeOffset, CancellationToken,
        Task<GameBoardSnapshot?>> ReadBoardQuery =
        EF.CompileAsyncQuery((PlatformDbContext db, Guid id, Guid playerId, GameParticipantKind kind, long? afterRevision, DateTimeOffset now,
            CancellationToken _) =>
            db.Games.AsNoTracking()
                .Where(game => game.Id == id
                    && ((game.WhitePlayerId == playerId && game.WhitePlayerKind == kind)
                        || (game.BlackPlayerId == playerId && game.BlackPlayerKind == kind)))
                .Select(game => new
                {
                    Game = game,
                    Expired = game.Status == GameStatus.Pending && game.InvitationExpiresAt <= now
                })
                .Select(item => new { item.Game, item.Expired, Revision = item.Game.Revision + (item.Expired ? 1 : 0) })
                .Select(item => new GameBoardSnapshot(item.Game.Id,
                    new GameParticipant(item.Game.WhitePlayerId, item.Game.WhitePlayerKind),
                    new GameParticipant(item.Game.BlackPlayerId, item.Game.BlackPlayerKind),
                    item.Expired ? GameStatus.Expired : item.Game.Status, item.Revision, item.Game.Fen, item.Game.SideToMove,
                    item.Game.Winner, item.Game.EndReason, item.Game.DrawOfferedBy, item.Game.InvitationExpiresAt,
                    item.Game.FinishedAt, item.Game.RematchOfferedBy, item.Game.RematchGameId,
                    item.Game.TimeControl, item.Game.WhiteTimeRemaining, item.Game.BlackTimeRemaining, item.Game.TurnDeadline,
                    item.Revision == afterRevision ? null : item.Game.RulesRecord, item.Game.WhiteGuestName, item.Game.BlackGuestName))
                .SingleOrDefault());

    public Task<Game?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        database.Games.AsNoTracking().SingleOrDefaultAsync(game => game.Id == id, cancellationToken);

    public async Task<IReadOnlyList<GameMoveTiming>> ReadMoveTimingsAsync(Guid gameId, CancellationToken cancellationToken) =>
        await database.GameMoveTimings.AsNoTracking().Where(timing => timing.GameId == gameId)
            .OrderBy(timing => timing.Ply).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Guid>> ListDueAsync(Guid playerId, DateTimeOffset now, CancellationToken cancellationToken) =>
        await GamesForAccount(playerId)
            .Where(game => game.Status == GameStatus.Active && game.TurnDeadline <= now)
            .Select(game => game.Id).ToListAsync(cancellationToken);

    public Task<GameBoardSnapshot?> ReadBoardAsync(Guid id, GameParticipant player, long? afterRevision,
        CancellationToken cancellationToken) =>
        ReadBoardQuery(database, id, player.Id, player.Kind, afterRevision, clock.GetUtcNow(), cancellationToken);

    public Task<Game?> FindByCreationRequestAsync(Guid creatorId, Guid requestId, CancellationToken cancellationToken) =>
        database.Games.AsNoTracking().SingleOrDefaultAsync(game => game.WhitePlayerId == creatorId
            && EF.Property<Guid?>(game, PlatformDbContext.GameCreationRequestId) == requestId, cancellationToken);

    public async Task<IReadOnlyList<GameListItem>> ListAsync(Guid playerId, GameListFilter filter, Guid? before, int limit,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.GetUtcNow();
        IQueryable<Game> participantGames = GamesForAccount(playerId);
        IQueryable<PrivateLobby> ownerLobbies = database.PrivateLobbies.AsNoTracking()
            .Where(lobby => lobby.CreatorId == playerId);
        IQueryable<PrivateLobby>? lobbies = filter switch
        {
            GameListFilter.Pending => ownerLobbies.Where(lobby => lobby.Status == LobbyStatus.Open && lobby.ExpiresAt > now),
            GameListFilter.ClosedInvitations => ownerLobbies.Where(lobby => lobby.Status == LobbyStatus.Cancelled
                || (lobby.Status == LobbyStatus.Open && lobby.ExpiresAt <= now)),
            _ => null
        };
        IQueryable<Game> games = filter switch
        {
            GameListFilter.Pending => participantGames.Where(game => game.Status == GameStatus.Pending
                && game.InvitationExpiresAt > now),
            GameListFilter.Active => participantGames.Where(game => game.Status == GameStatus.Active),
            GameListFilter.Finished => participantGames.Where(game => game.Status == GameStatus.Finished),
            GameListFilter.ClosedInvitations => participantGames.Where(game =>
                game.Status == GameStatus.Declined || game.Status == GameStatus.Cancelled
                || (game.Status == GameStatus.Pending && game.InvitationExpiresAt <= now)),
            _ => throw new ArgumentOutOfRangeException(nameof(filter))
        };
        if (before is Guid cursor)
        {
            // Resolve the cursor even if that game moved to another status after the last read.
            var boundary = await participantGames.Where(game => game.Id == cursor)
                .Select(game => new { game.CreatedAt }).SingleOrDefaultAsync(cancellationToken);
            if (boundary is null && lobbies is not null)
            {
                boundary = await ownerLobbies.Where(lobby => lobby.Id == cursor)
                    .Select(lobby => new { CreatedAt = (DateTimeOffset?)lobby.CreatedAt })
                    .SingleOrDefaultAsync(cancellationToken);
            }
            if (boundary is null)
            {
                return [];
            }

            games = boundary.CreatedAt is DateTimeOffset createdAt
                ? games.Where(game => game.CreatedAt == null || game.CreatedAt < createdAt
                    || (game.CreatedAt == createdAt && game.Id.CompareTo(cursor) < 0))
                : games.Where(game => game.CreatedAt == null && game.Id.CompareTo(cursor) < 0);
            if (lobbies is not null)
            {
                lobbies = boundary.CreatedAt is DateTimeOffset lobbyBoundary
                    ? lobbies.Where(lobby => lobby.CreatedAt < lobbyBoundary
                        || (lobby.CreatedAt == lobbyBoundary && lobby.Id.CompareTo(cursor) < 0))
                    : lobbies.Where(lobby => false);
            }
        }

        List<GameListItem> gameItems = await games.OrderBy(game => game.CreatedAt == null).ThenByDescending(game => game.CreatedAt)
            .ThenByDescending(game => game.Id).Take(limit)
            .Select(game => new GameListItem(game.Id,
                game.WhitePlayerId == playerId ? game.BlackPlayerId : game.WhitePlayerId,
                game.WhitePlayerId == playerId ? PlayerColor.White : PlayerColor.Black,
                game.Status == GameStatus.Pending && game.InvitationExpiresAt <= now ? GameStatus.Expired : game.Status,
                game.CreatedAt, game.InvitationExpiresAt,
                game.Status == GameStatus.Active && (game.SideToMove == PlayerColor.White
                    ? game.WhitePlayerId == playerId : game.BlackPlayerId == playerId), game.Winner, game.EndReason,
                false, game.TimeControl, game.WhitePlayerId == playerId ? game.BlackGuestName : game.WhiteGuestName,
                game.WhitePlayerId == playerId ? game.BlackPlayerKind : game.WhitePlayerKind))
            .ToListAsync(cancellationToken);
        if (lobbies is null)
        {
            return gameItems;
        }

        List<GameListItem> lobbyItems = await lobbies.OrderByDescending(lobby => lobby.CreatedAt)
            .ThenByDescending(lobby => lobby.Id).Take(limit)
            .Select(lobby => new GameListItem(lobby.Id, null, PlayerColor.White,
                lobby.Status == LobbyStatus.Cancelled ? GameStatus.Cancelled
                    : lobby.ExpiresAt <= now ? GameStatus.Expired : GameStatus.Pending,
                lobby.CreatedAt, lobby.ExpiresAt, false, null, null, true, lobby.TimeControl))
            .ToListAsync(cancellationToken);
        return gameItems.Concat(lobbyItems).OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id).Take(limit).ToArray();
    }

    private IQueryable<Game> GamesForAccount(Guid playerId) => database.Games.AsNoTracking()
        .Where(game => (game.WhitePlayerId == playerId && game.WhitePlayerKind == GameParticipantKind.Account)
            || (game.BlackPlayerId == playerId && game.BlackPlayerKind == GameParticipantKind.Account));

    public async Task<CreateGameResult> CreateAsync(Guid creatorId, Guid opponentId, GamePosition startingPosition,
        Guid? requestId, CancellationToken cancellationToken, GameTimeControl? timeControl)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await PostgresInvitationQuota.LockCreatorAsync(database, creatorId, cancellationToken);

        if (requestId is Guid key
            && await FindByCreationRequestAsync(creatorId, key, cancellationToken) is { } existing)
        {
            return CreateGameResult.Replay(existing, opponentId, timeControl);
        }

        DateTimeOffset now = PostgresInvitationQuota.GetCreationTime(clock);
        IQueryable<Game> sentInvitations = database.Games.AsNoTracking()
            .Where(game => game.WhitePlayerId == creatorId && game.CreationOrigin == GameCreationOrigin.DirectInvitation);
        IQueryable<Game> pending = sentInvitations.Where(game => game.Status == GameStatus.Pending && game.InvitationExpiresAt > now);
        Game? duplicate = await pending.Where(game => game.BlackPlayerId == opponentId)
            .OrderByDescending(game => game.CreatedAt).ThenByDescending(game => game.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (duplicate is not null)
        {
            return new(GameError.InvitationAlreadyPending, duplicate);
        }

        DateTimeOffset cutoff = now - PairWindow;
        DateTimeOffset? windowStart = await sentInvitations.Where(game => game.BlackPlayerId == opponentId && game.CreatedAt > cutoff)
            .OrderByDescending(game => game.CreatedAt).Skip(MaxPairInvitations - 1)
            .Select(game => game.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        if (windowStart is { } first)
        {
            return new(GameError.InvitationRateLimited, RetryAfter: first + PairWindow - now);
        }

        if (!await PostgresInvitationQuota.HasCapacityAsync(database, creatorId, now, cancellationToken))
        {
            return new(GameError.InvitationLimitReached);
        }

        Game game = new(creatorId, opponentId, startingPosition, now, timeControl);
        var entry = database.Games.Add(game);
        entry.Property<Guid?>(PlatformDbContext.GameCreationRequestId).CurrentValue = requestId;
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(GameError.None, game, Created: true);
    }

    public Task<GameOperationResult> ChangeLockedAsync(Guid gameId, GameParticipant player,
        Func<Game, DateTimeOffset, GameError> change, CancellationToken cancellationToken)
        => ChangeLockedAsync(gameId, player, (game, now) => (change(game, now), null, null), cancellationToken);

    public Task<GameOperationResult> PlayAsync(Guid gameId, GameParticipant player,
        Func<Game, DateTimeOffset, (GameError Error, GameMoveTiming? Timing)> play, CancellationToken cancellationToken) =>
        ChangeLockedAsync(gameId, player, (game, now) =>
        {
            (GameError error, GameMoveTiming? timing) = play(game, now);
            return (error, null, timing);
        }, cancellationToken);

    public Task<GameOperationResult> AcceptRematchAsync(Guid gameId, GameParticipant player, long expectedRevision,
        Func<GamePosition> startingPosition, CancellationToken cancellationToken) =>
        ChangeLockedAsync(gameId, player,
            (game, now) =>
            {
                (GameError error, Game? rematch) = game.AcceptRematch(player, expectedRevision, now, startingPosition);
                return (error, rematch, null);
            }, cancellationToken);

    private async Task<GameOperationResult> ChangeLockedAsync(Guid gameId, GameParticipant player,
        Func<Game, DateTimeOffset, (GameError Error, Game? Rematch, GameMoveTiming? Timing)> change,
        CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        Game? game = await database.Games.FromSql($"""
            SELECT * FROM "Games"
            WHERE "Id" = {gameId}
                AND (("WhitePlayerId" = {player.Id} AND "WhitePlayerKind" = {player.Kind.ToString()})
                    OR ("BlackPlayerId" = {player.Id} AND "BlackPlayerKind" = {player.Kind.ToString()}))
            FOR UPDATE
            """).SingleOrDefaultAsync(cancellationToken);
        if (game is null)
        {
            return new(GameError.NotFound);
        }
        (GameError error, Game? rematch, GameMoveTiming? timing) = change(game, clock.GetUtcNow());
        if (error != GameError.None)
        {
            return new(error);
        }

        if (rematch is not null)
        {
            database.Games.Add(rematch);
        }
        if (timing is not null)
        {
            database.GameMoveTimings.Add(timing);
        }
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(GameError.None, game);
    }
}
