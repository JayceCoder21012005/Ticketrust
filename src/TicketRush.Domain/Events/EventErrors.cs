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

    public static readonly Error VenueNotFound = Error.NotFound("venue.not_found", "Venue not found.");
}
