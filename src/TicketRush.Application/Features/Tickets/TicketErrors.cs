using TicketRush.Domain.Primitives;

namespace TicketRush.Application.Features.Tickets;

public static class TicketErrors
{
    /// <summary>Also returned for another user's ticket, so ticket ids cannot be probed.</summary>
    public static readonly Error NotFound = Error.NotFound("ticket.not_found", "Ticket not found.");

    /// <summary>The ticket is not <c>Valid</c>, or its event is not published, has ended or was cancelled.</summary>
    public static readonly Error NotDisplayable = Error.Conflict("ticket.not_displayable", "This ticket cannot show a QR code.");

    /// <summary>Carries <see cref="CommonErrors.RetryAfterExtension"/>; the API turns it into the <c>Retry-After</c> header.</summary>
    public static Error QrKeyRateLimited(TimeSpan retryAfter) =>
        new(
            "ticket.qr_key_rate_limited",
            "Too many QR key requests. Please try again shortly.",
            ErrorType.RateLimited,
            Extensions: new Dictionary<string, object?> { [CommonErrors.RetryAfterExtension] = (int)Math.Ceiling(retryAfter.TotalSeconds) });
}
