namespace TicketRush.Domain.Events.Publishing.Rules;

public sealed class GaTiersHaveCapacityRule : IPublishRule
{
    public PublishFailure? Check(PublishContext context) =>
        Lacking(context.Event.Tiers.Select(tier => (tier.Kind, tier.Capacity)))
            ? new PublishFailure("publish.ga_without_capacity", "tiers", "Every standing tier needs a capacity above zero.")
            : null;

    public static bool Lacking(IEnumerable<(TicketKind Kind, int? Capacity)> tiers) =>
        tiers.Any(tier => tier.Kind == TicketKind.GeneralAdmission && tier.Capacity is not > 0);
}
