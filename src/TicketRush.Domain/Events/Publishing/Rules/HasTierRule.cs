namespace TicketRush.Domain.Events.Publishing.Rules;

public sealed class HasTierRule : IPublishRule
{
    public PublishFailure? Check(PublishContext context) =>
        context.Event.Tiers.Count == 0
            ? new PublishFailure("publish.no_tiers", "tiers", "Add at least one ticket tier.")
            : null;
}
