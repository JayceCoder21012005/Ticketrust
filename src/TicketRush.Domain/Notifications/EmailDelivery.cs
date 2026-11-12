using TicketRush.Domain.Primitives;

namespace TicketRush.Domain.Notifications;

/// <summary>
/// An email the system owes a user, kept as a row so the in-app notice never waits on SMTP: the delivery worker sends
/// <see cref="EmailDeliveryStatus.Pending"/> rows that are due and records each outcome here. The worker, not this class, decides the retry
/// delay and when to give up (it passes both to <see cref="RecordFailure"/>).
/// </summary>
public sealed class EmailDelivery : Entity
{
    /// <summary>Column width of <see cref="LastError"/>.</summary>
    public const int MaxErrorLength = 2000;

    /// <summary>Stored as <see cref="LastError"/> when a failure comes with a blank message.</summary>
    public const string UnknownError = "unknown error";

    /// <summary>Stored as <see cref="LastError"/> when the delivery was parked unsent because its moment had passed (<see cref="MarkExpired"/>).</summary>
    public const string ExpiredError = "expired";
    public const int MaxToAddressLength = 320;
    public const int MaxTemplateLength = 100;
    public const int MaxDedupeKeyLength = 200;

    private EmailDelivery(Guid userId, string toAddress, string template, string modelJson, string dedupeKey, DateTimeOffset now, DateTimeOffset? expiresAt)
        : base(Guid.CreateVersion7())
    {
        UserId = userId;
        ToAddress = toAddress;
        Template = template;
        ModelJson = modelJson;
        DedupeKey = dedupeKey;
        Status = EmailDeliveryStatus.Pending;
        NextAttemptAt = now;
        CreatedAt = now;
        ExpiresAt = expiresAt;
    }

    // Required by EF Core.
    private EmailDelivery()
    {
        ToAddress = string.Empty;
        Template = string.Empty;
        ModelJson = string.Empty;
        DedupeKey = string.Empty;
    }

    public Guid UserId { get; private set; }

    public string ToAddress { get; private set; }

    /// <summary>Name of the template pair (subject and body) the sender renders.</summary>
    public string Template { get; private set; }

    /// <summary>The template's model as JSON (stored as <c>jsonb</c>).</summary>
    public string ModelJson { get; private set; }

    public string DedupeKey { get; private set; }

    public EmailDeliveryStatus Status { get; private set; }

    /// <summary>Failed sends so far.</summary>
    public int Attempts { get; private set; }

    /// <summary>Earliest next try; equals the creation time until a send fails.</summary>
    public DateTimeOffset NextAttemptAt { get; private set; }

    /// <summary>The sender's last failure, cut to <see cref="MaxErrorLength"/>. Kept after a later success, for the record.</summary>
    public string? LastError { get; private set; }

    public DateTimeOffset? SentAt { get; private set; }

    /// <summary>When the email was queued: how old a backlog is, whatever its retries did to <see cref="NextAttemptAt"/>.</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    /// The moment after which the email is no longer true (a reminder of a sale opening that has already opened), or <c>null</c> for
    /// an email that is worth sending however late.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; private set; }

    /// <param name="expiresAt">See <see cref="ExpiresAt"/>; <c>null</c> = never expires.</param>
    public static EmailDelivery Create(Guid userId, string toAddress, string template, string modelJson, string dedupeKey, DateTimeOffset now, DateTimeOffset? expiresAt = null)
    {
        var address = toAddress?.Trim();
        var templateName = template?.Trim();
        var key = dedupeKey?.Trim();

        if (userId == Guid.Empty
            || string.IsNullOrEmpty(address) || address.Length > MaxToAddressLength
            || string.IsNullOrEmpty(templateName) || templateName.Length > MaxTemplateLength
            || string.IsNullOrWhiteSpace(modelJson)
            || string.IsNullOrEmpty(key) || key.Length > MaxDedupeKeyLength)
        {
            throw new DomainException(
                "email_delivery.invalid",
                "An email delivery needs a user, an address, a template name, a model and a dedupe key within the column widths.");
        }

        return new EmailDelivery(userId, address, templateName, modelJson, key, now, expiresAt);
    }

    /// <summary>Pending → Sent.</summary>
    public void MarkSent(DateTimeOffset now)
    {
        EnsurePending(nameof(MarkSent));
        Status = EmailDeliveryStatus.Sent;
        SentAt = now;
    }

    /// <summary>
    /// One more failed send: counts it and keeps the error. With <paramref name="deadLetter"/> the delivery is parked
    /// (<see cref="EmailDeliveryStatus.DeadLettered"/>); otherwise it stays pending and is due again at
    /// <c>now + delay</c>.
    /// </summary>
    public void RecordFailure(DateTimeOffset now, string error, TimeSpan delay, bool deadLetter)
    {
        EnsurePending(nameof(RecordFailure));
        Attempts++;

        // Some exceptions carry an empty message; recording the failure must never throw or the row would stay due forever.
        var reason = string.IsNullOrWhiteSpace(error) ? UnknownError : error;
        LastError = reason.Length <= MaxErrorLength ? reason : reason[..MaxErrorLength];
        if (deadLetter)
        {
            Status = EmailDeliveryStatus.DeadLettered;
        }
        else
        {
            NextAttemptAt = now + delay;
        }
    }

    public bool IsExpired(DateTimeOffset now) => ExpiresAt is { } expiresAt && expiresAt <= now;

    /// <summary>
    /// Pending → DeadLettered without a send: what it says is no longer true. Not a failed attempt (<see cref="Attempts"/> is
    /// unchanged); <see cref="LastError"/> becomes <see cref="ExpiredError"/>, which tells it apart from a delivery that failed.
    /// </summary>
    public void MarkExpired()
    {
        EnsurePending(nameof(MarkExpired));
        Status = EmailDeliveryStatus.DeadLettered;
        LastError = ExpiredError;
    }

    private void EnsurePending(string method)
    {
        if (Status != EmailDeliveryStatus.Pending)
        {
            throw new DomainException("email_delivery.illegal_transition", $"Cannot {method} an email delivery that is {Status}.");
        }
    }
}
