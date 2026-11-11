using TicketRush.Domain.Primitives;

namespace TicketRush.Domain.Notifications;

/// <summary>
/// One in-app notice for one user. <see cref="DedupeKey"/> comes from the business entity (for example
/// <c>order-paid:{orderId}</c>), never from a message id, so a re-emitted event cannot add a second notice.
/// </summary>
public sealed class Notification : Entity
{
    public const int MaxTitleLength = 200;
    public const int MaxBodyLength = 2000;
    public const int MaxLinkLength = 500;
    public const int MaxDedupeKeyLength = 200;

    private Notification(Guid userId, NotificationType type, string title, string body, string? link, string dedupeKey, DateTimeOffset now)
        : base(Guid.CreateVersion7())
    {
        UserId = userId;
        Type = type;
        Title = title;
        Body = body;
        Link = link;
        DedupeKey = dedupeKey;
        CreatedAt = now;
    }

    // Required by EF Core.
    private Notification()
    {
        Title = string.Empty;
        Body = string.Empty;
        DedupeKey = string.Empty;
    }

    public Guid UserId { get; private set; }

    public NotificationType Type { get; private set; }

    public string Title { get; private set; }

    public string Body { get; private set; }

    /// <summary>App-relative path the notice opens, or <c>null</c>.</summary>
    public string? Link { get; private set; }

    public string DedupeKey { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? ReadAt { get; private set; }

    public static Notification Create(Guid userId, NotificationType type, string title, string body, string? link, string dedupeKey, DateTimeOffset now)
    {
        var cleanTitle = title?.Trim();
        var cleanBody = body?.Trim();
        var cleanLink = string.IsNullOrWhiteSpace(link) ? null : link.Trim();
        var cleanKey = dedupeKey?.Trim();

        if (userId == Guid.Empty
            || string.IsNullOrEmpty(cleanTitle) || cleanTitle.Length > MaxTitleLength
            || string.IsNullOrEmpty(cleanBody) || cleanBody.Length > MaxBodyLength
            || cleanLink?.Length > MaxLinkLength
            || string.IsNullOrEmpty(cleanKey) || cleanKey.Length > MaxDedupeKeyLength)
        {
            throw new DomainException(
                "notification.invalid",
                $"A notification needs a user, a title (up to {MaxTitleLength}), a body (up to {MaxBodyLength}), a link of up to {MaxLinkLength} and a dedupe key (up to {MaxDedupeKeyLength}).");
        }

        return new Notification(userId, type, cleanTitle, cleanBody, cleanLink, cleanKey, now);
    }

    /// <summary>Idempotent: reading twice keeps the first <see cref="ReadAt"/>.</summary>
    public void MarkRead(DateTimeOffset now) => ReadAt ??= now;
}
