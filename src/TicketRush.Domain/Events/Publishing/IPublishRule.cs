namespace TicketRush.Domain.Events.Publishing;

/// <param name="Code">Stable error code, e.g. <c>publish.missing_cover</c>.</param>
/// <param name="Step">The wizard step that fixes it: <c>info</c>, <c>tiers</c>, <c>layout</c> or <c>phases</c>.</param>
public sealed record PublishFailure(string Code, string Step, string Message);

/// <param name="SeatsPerTier">Seat count per seated tier, counted from <c>event_seats</c>.</param>
/// <param name="Now">Server time.</param>
public sealed record PublishContext(Event Event, IReadOnlyDictionary<Guid, int> SeatsPerTier, DateTimeOffset Now);

/// <summary>One condition an event must meet before it can be published (spec 05). Returns null when it holds.</summary>
public interface IPublishRule
{
    PublishFailure? Check(PublishContext context);
}
