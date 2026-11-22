using System.Text.Json;
using System.Text.Json.Serialization;
using TicketRush.Application.Abstractions.Payments;
using TicketRush.Domain.Primitives;

namespace TicketRush.Infrastructure.Payments;

/// <summary>What the dev/demo user chose on the mock gateway page (spec 08).</summary>
public enum MockOutcome
{
    /// <summary>Paid; the webhook is delivered right away.</summary>
    [JsonStringEnumMemberName("succeeded")]
    Succeeded,

    /// <summary>Declined; the webhook is delivered right away.</summary>
    [JsonStringEnumMemberName("failed")]
    Failed,

    /// <summary>Paid now, but the webhook only arrives <see cref="MockGatewayOptions.DelayedWebhookDelay"/> later (C3).</summary>
    [JsonStringEnumMemberName("succeeded_delayed")]
    SucceededDelayed,

    /// <summary>Paid; the identical webhook is delivered twice (C4).</summary>
    [JsonStringEnumMemberName("succeeded_twice")]
    SucceededTwice,
}

/// <summary>What the mock stores for a session. <see cref="Expired"/> is never stored: it is derived from the expiry.</summary>
public enum MockSessionStatus
{
    Pending,
    Succeeded,
    Failed,
}

/// <summary>A session as stored in Redis. <paramref name="RefundedAmount"/> is whole dong already refunded.</summary>
public sealed record MockSession(
    string SessionId,
    Guid OrderId,
    Guid EventId,
    string Label,
    Money Amount,
    Uri ReturnUrl,
    DateTimeOffset ExpiresAt,
    DateTimeOffset CreatedAt,
    MockSessionStatus Status,
    long RefundedAmount,
    string? TraceParent = null)
{
    /// <summary>The status a caller sees: a pending session past its expiry is expired.</summary>
    public PaymentStatus EffectiveStatus(DateTimeOffset now) => Status switch
    {
        MockSessionStatus.Succeeded => PaymentStatus.Succeeded,
        MockSessionStatus.Failed => PaymentStatus.Failed,
        _ => now >= ExpiresAt ? PaymentStatus.Expired : PaymentStatus.Pending,
    };
}

/// <summary>One webhook waiting in the delivery queue. The body is signed fresh on every attempt, never stored signed.</summary>
/// <summary>One webhook to send; <see cref="TraceParent"/> is the pay request's trace, which the delivery continues (spec 17).</summary>
public sealed record MockDelivery(string DeliveryId, string Body, int Attempt, string? TraceParent = null);

/// <summary>The JSON body of every mock webhook. Field names are the mock provider's contract with <c>VerifyAndParse</c>.</summary>
public sealed record MockWebhookPayload(
    string Id,
    string Type,
    string SessionId,
    long? Amount,
    string? Currency,
    DateTimeOffset OccurredAt)
{
    public const string SucceededType = "payment.succeeded";
    public const string FailedType = "payment.failed";
    public const string RefundedType = "payment.refunded";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string TypeName(WebhookEventType type) => type switch
    {
        WebhookEventType.Succeeded => SucceededType,
        WebhookEventType.Failed => FailedType,
        _ => RefundedType,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);
}

/// <summary>A session plus the clock reading the web page needs to render its countdown.</summary>
public sealed record MockSessionView(
    string SessionId,
    Guid OrderId,
    Guid EventId,
    string Label,
    long Amount,
    string Currency,
    PaymentStatus Status,
    DateTimeOffset ExpiresAt,
    Uri ReturnUrl,
    DateTimeOffset ServerNow);

public static class MockGatewayErrors
{
    public static readonly Error SessionNotFound =
        Error.NotFound("mock_gateway.session_not_found", "This payment session does not exist or has been cleaned up.");

    public static readonly Error SessionExpired =
        Error.Conflict("mock_gateway.session_expired", "This payment session has expired.");

    public static readonly Error AlreadyCompleted =
        Error.Conflict("mock_gateway.already_completed", "This payment session was already completed.");
}
