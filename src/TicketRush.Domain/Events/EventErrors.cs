using TicketRush.Domain.Primitives;

namespace TicketRush.Domain.Events;

public static class EventErrors
{
    /// <summary>Also returned for draft and cancelled events, so unpublished events can't be probed.</summary>
    public static readonly Error NotFound = Error.NotFound("event.not_found", "Event not found.");

    public static readonly Error ConcurrentEdit =
        Error.Conflict("event.concurrent_edit", "The event was changed somewhere else. Reload and try again.");

    public static readonly Error CoverNotOwned =
        Error.Validation("event.cover_not_owned", "The cover must be uploaded through this event's upload link.");

    /// <summary>
    /// The cancel waited longer than the lock timeout for payments being confirmed right now; nothing changed. 409, not 5xx:
    /// the organizer simply tries again a moment later.
    /// </summary>
    public static readonly Error CancelBusy =
        Error.Conflict("event.cancel_busy", "Payments for this event are being confirmed right now. Please try cancelling again in a moment.");

    public static readonly Error VenueNotFound = Error.NotFound("venue.not_found", "Venue not found.");
}
