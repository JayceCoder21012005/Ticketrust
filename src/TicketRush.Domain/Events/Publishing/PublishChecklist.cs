using TicketRush.Domain.Events.Publishing.Rules;

namespace TicketRush.Domain.Events.Publishing;

/// <summary>
/// Everything that must hold before <see cref="Event.Publish"/>. The organizer publish handler runs it first; the wizard
/// shows the same list, so the rules and their order here are what the organizer sees.
/// </summary>
public static class PublishChecklist
{
    public static IReadOnlyList<IPublishRule> Rules { get; } =
    [
        new HasCoverRule(),
        new HasTierRule(),
        new SeatedTiersHaveSeatsRule(),
        new GaTiersHaveCapacityRule(),
        new HasPhaseRule(),
        new FirstPhaseOpensLaterRule(),
        new PhasesDoNotOverlapRule(),
        new PhasesCloseBeforeStartRule(),
    ];

    public static IReadOnlyList<PublishFailure> Run(PublishContext context) =>
        Rules.Select(rule => rule.Check(context)).OfType<PublishFailure>().ToList();
}
