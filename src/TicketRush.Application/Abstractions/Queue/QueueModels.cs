namespace TicketRush.Application.Abstractions.Queue;

/// <summary>Room-wide state of one sale phase's waiting room (spec 06).</summary>
public enum RoomStatus
{
    Pre,
    Opening,
    Open,
    AllHeld,
    SoldOut,
    Closed,
}

/// <summary>The part of a <c>SalePhase</c> the waiting room needs, so Redis code never touches EF entities.</summary>
public sealed record SaleWindow(Guid EventId, Guid PhaseId, DateTimeOffset OpensAt, DateTimeOffset ClosesAt, bool QueueEnabled);

/// <param name="Number">Null while the user sits in the pre-queue (no number until the shuffle) or the sale is closed.</param>
public sealed record JoinResult(RoomStatus Status, long? Number);

/// <param name="Claimed">True when this very call moved the user into the active set.</param>
/// <param name="Active">The user currently holds an admission.</param>
/// <param name="Done">The user left, or their shopping window ended; they must rejoin at the tail.</param>
/// <param name="InPre">The user joined before opening and has no number yet.</param>
public sealed record ClaimResult(
    RoomStatus Status,
    long? Number,
    long Admitted,
    long Seq,
    long ActiveCount,
    bool Claimed,
    bool Active,
    bool Done,
    bool InPre,
    DateTimeOffset? ActiveUntil,
    string? Token);

/// <summary>Redis could not be reached. Callers fail closed (503 <c>system.busy</c>) and never admit anyone.</summary>
public sealed class QueueUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
