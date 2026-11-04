namespace TicketRush.Domain.Events.Publishing.Rules;

public sealed class HasCoverRule : IPublishRule
{
    public PublishFailure? Check(PublishContext context) =>
        string.IsNullOrWhiteSpace(context.Event.CoverImageUrl)
            ? new PublishFailure("publish.missing_cover", "info", "Add a cover image.")
            : null;
}
