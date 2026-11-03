using TicketRush.Domain.Primitives;

namespace TicketRush.Domain.Events;

public static class EventErrors
{
    /// <summary>Also returned for draft and cancelled events, so unpublished events can't be probed.</summary>
    public static readonly Error NotFound = Error.NotFound("event.not_found", "Event not found.");
}
