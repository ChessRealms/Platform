namespace ChessRealms.Platform.Domain.Games;

public enum GameError
{
    None,
    NotFound,
    InvalidPlayers,
    OpponentUnavailable,
    Forbidden,
    RevisionConflict,
    GameNotPending,
    GameNotActive,
    NotYourTurn,
    IllegalMove,
    DrawOfferPending,
    NoDrawOffer,
    DrawClaimUnavailable,
    InvalidRequestId,
    CreationRequestConflict
}
