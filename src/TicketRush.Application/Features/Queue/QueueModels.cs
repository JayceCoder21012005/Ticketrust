using TicketRush.Domain.Primitives;

namespace TicketRush.Application.Features.Queue;

/// <summary>What a buyer's client shows. Serialised as a string.</summary>
public enum QueueClientStatus
{
    /// <summary>In the pre-queue: the order is decided at opening time.</summary>
    Pre,
    Waiting,
    Admitted,
    AllHeld,
    SoldOut,
    Closed,

    /// <summary>The shopping window ended, or the buyer left. They must join again.</summary>
    Expired,
}

public sealed record JoinQueueCommand(Guid UserId, Guid EventId, bool EmailVerified, string? AccessCode);

/// <param name="Position">1 = next in line. Null outside <see cref="QueueClientStatus.Waiting"/>.</param>
/// <param name="ServerNow">Lets the client correct its clock for countdowns (C10).</param>
/// <param name="PollAfterMs">When the client should ask again; chosen by the server so it can shed load.</param>
public sealed record QueueStatusResponse(
    QueueClientStatus Status,
    long? Position,
    long? AheadOfYou,
    int? EstimatedWaitSeconds,
    DateTimeOffset OpensAt,
    DateTimeOffset ServerNow,
    int PollAfterMs,
    string? AdmissionToken,
    DateTimeOffset? TokenExpiresAt);

public static class QueueErrors
{
    public static readonly Error NotOpen = Error.Conflict("queue.not_open", "The waiting room is not open yet.");

    public static readonly Error Closed = Error.Conflict("queue.closed", "This sale has closed.");

    public static readonly Error InvalidAccessCode = Error.Forbidden("queue.invalid_access_code", "That presale code is not valid.");

    public static readonly Error EmailNotConfirmed =
        Error.Forbidden("queue.email_not_confirmed", "Confirm your email address before joining the waiting room.");

    public static readonly Error NotJoined = Error.NotFound("queue.not_joined", "You have not joined this waiting room.");

    public static readonly Error AdmissionRequired =
        Error.Forbidden("admission.required", "You need an admission from the waiting room for this.");

    public static readonly Error AdmissionInvalid =
        Error.Forbidden("admission.invalid", "Your admission is not valid for this event, or it has ended.");
}
