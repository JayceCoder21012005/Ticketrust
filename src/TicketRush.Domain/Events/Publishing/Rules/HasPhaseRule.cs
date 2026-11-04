namespace TicketRush.Domain.Events.Publishing.Rules;

public sealed class HasPhaseRule : IPublishRule
{
    public PublishFailure? Check(PublishContext context) =>
        context.Event.Phases.Count == 0
            ? new PublishFailure("publish.no_phases", "phases", "Add at least one sale phase.")
            : null;
}
