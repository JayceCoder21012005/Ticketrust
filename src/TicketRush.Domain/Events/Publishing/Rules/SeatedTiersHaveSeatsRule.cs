namespace TicketRush.Domain.Events.Publishing.Rules;

public sealed class SeatedTiersHaveSeatsRule : IPublishRule
{
    public PublishFailure? Check(PublishContext context)
    {
        var empty = context.Event.Tiers
            .Where(tier => tier.Kind == TicketKind.Seated && context.SeatsPerTier.GetValueOrDefault(tier.Id) == 0)
            .Select(tier => tier.Name)
            .ToList();

        return empty.Count == 0
            ? null
            : new PublishFailure("publish.tier_without_seats", "layout", $"Seated tiers without seats: {string.Join(", ", empty)}.");
    }
}
