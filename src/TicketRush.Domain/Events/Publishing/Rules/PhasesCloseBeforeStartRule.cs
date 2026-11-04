namespace TicketRush.Domain.Events.Publishing.Rules;

/// <summary>The aggregate already refuses phases closing after the start; this guards rows written outside it.</summary>
public sealed class PhasesCloseBeforeStartRule : IPublishRule
{
    public PublishFailure? Check(PublishContext context) =>
        AnyClosesAfter(context.Event.StartsAt, context.Event.Phases.Select(phase => phase.ClosesAt))
            ? new PublishFailure("phase.after_event_start", "phases", "Sale phases must close before the event starts.")
            : null;

    public static bool AnyClosesAfter(DateTimeOffset startsAt, IEnumerable<DateTimeOffset> closes) =>
        closes.Any(closesAt => closesAt > startsAt);
}
