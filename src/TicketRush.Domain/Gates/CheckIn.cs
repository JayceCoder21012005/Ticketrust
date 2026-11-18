using TicketRush.Domain.Primitives;

namespace TicketRush.Domain.Gates;

/// <summary>
/// One line of the gate log (spec 11): every scan is recorded, admitted or not. <see cref="TicketId"/> is null when the code
/// did not identify a ticket (<see cref="ScanResult.Invalid"/>). <see cref="Reason"/> is a short machine code, never the payload.
/// </summary>
public sealed class CheckIn : Entity
{
    public const int ReasonMaxLength = 40;

    private CheckIn(
        Guid? ticketId,
        Guid eventId,
        Guid staffUserId,
        string gateName,
        DateTimeOffset scannedAt,
        ScanResult result,
        string? reason)
        : base(Guid.CreateVersion7())
    {
        TicketId = ticketId;
        EventId = eventId;
        StaffUserId = staffUserId;
        GateName = gateName;
        ScannedAt = scannedAt;
        Result = result;
        Reason = reason;
    }

    // Required by EF Core.
    private CheckIn()
    {
    }

    public Guid? TicketId { get; private set; }

    public Guid EventId { get; private set; }

    public Guid StaffUserId { get; private set; }

    public string GateName { get; private set; } = string.Empty;

    public DateTimeOffset ScannedAt { get; private set; }

    public ScanResult Result { get; private set; }

    public string? Reason { get; private set; }

    public static CheckIn Record(
        Guid? ticketId,
        Guid eventId,
        Guid staffUserId,
        string gateName,
        DateTimeOffset scannedAt,
        ScanResult result,
        string? reason = null)
    {
        if (eventId == Guid.Empty || staffUserId == Guid.Empty)
        {
            throw new DomainException("checkin.invalid_reference", "A check-in log line needs an event and a staff user.");
        }

        if (reason is { Length: > ReasonMaxLength })
        {
            throw new DomainException("checkin.invalid_reason", $"A reason is at most {ReasonMaxLength} characters.");
        }

        return new CheckIn(ticketId, eventId, staffUserId, gateName, scannedAt, result, reason);
    }
}
