namespace TicketRush.Domain.Events.Publishing.Rules;

/// <summary>The aggregate already refuses overlapping phases; this guards rows written outside it.</summary>
public sealed class PhasesDoNotOverlapRule : IPublishRule
{
    public PublishFailure? Check(PublishContext context) =>
        AnyOverlap(context.Event.Phases.Select(phase => (phase.OpensAt, phase.ClosesAt)))
            ? new PublishFailure("event.phase_overlap", "phases", "Sale phases must not overlap.")
            : null;

    public static bool AnyOverlap(IEnumerable<(DateTimeOffset Opens, DateTimeOffset Closes)> windows)
    {
        var ordered = windows.OrderBy(w => w.Opens).ToList();
        for (var i = 1; i < ordered.Count; i++)
        {
            if (ordered[i].Opens < ordered[i - 1].Closes)
            {
                return true;
            }
        }

        return false;
    }
}
