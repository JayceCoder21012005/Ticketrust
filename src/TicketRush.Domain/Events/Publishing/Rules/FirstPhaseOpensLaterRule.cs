namespace TicketRush.Domain.Events.Publishing.Rules;

/// <summary>Leaves time for the inventory warm-up (spec 07) and for buyers to see the countdown.</summary>
public sealed class FirstPhaseOpensLaterRule : IPublishRule
{
    public static readonly TimeSpan MinimumLead = TimeSpan.FromMinutes(10);

    public PublishFailure? Check(PublishContext context)
    {
        if (context.Event.Phases.Count == 0)
        {
            return null;
        }

        var first = context.Event.Phases.Min(phase => phase.OpensAt);
        return first - context.Now < MinimumLead
            ? new PublishFailure("publish.first_phase_too_soon", "phases", "The first sale phase must open at least 10 minutes from now.")
            : null;
    }
}
