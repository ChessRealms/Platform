namespace ChessRealms.Platform.Domain.Games;

public enum GameEndReason
{
    Checkmate,
    Stalemate,
    DeadPosition,
    FivefoldRepetition,
    SeventyFiveMoveRule,
    Resignation,
    DrawAgreement,
    ThreefoldRepetition,
    FiftyMoveRule,
    ResignationNoPossibleMate
}
