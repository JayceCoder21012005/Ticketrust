using Microsoft.Extensions.Options;
using TicketRush.Application.Abstractions.Payments;
using TicketRush.Domain.Primitives;

namespace TicketRush.Infrastructure.Payments;

/// <summary>
/// What the mock gateway page (dev/demo only) can do: read a session and pick its outcome.
/// <para>
/// The session status flips at <b>complete</b> time, for <c>succeeded_delayed</c> too: the money has moved and only the
/// notification is late. So <c>GetStatusAsync</c> already answers <c>Succeeded</c> while the webhook is still queued, which is
/// what the order expiry service must notice (C3). Completing is atomic with queuing the webhooks, so a session is never
/// completed without its notification.
/// </para>
/// </summary>
public sealed class MockGatewayControl(
    MockSessionStore sessions,
    IOptionsMonitor<MockGatewayOptions> options,
    TimeProvider time)
{
    public async Task<MockSessionView?> GetAsync(string sessionId, CancellationToken ct)
    {
        var session = await sessions.GetAsync(sessionId, ct);
        return session is null ? null : ToView(session, time.GetUtcNow());
    }

    public async Task<Result<MockSessionView>> CompleteAsync(string sessionId, MockOutcome outcome, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var session = await sessions.GetAsync(sessionId, ct);
        if (session is null)
        {
            return MockGatewayErrors.SessionNotFound;
        }

        if (session.EffectiveStatus(now) is var status && status != PaymentStatus.Pending)
        {
            return status == PaymentStatus.Expired ? MockGatewayErrors.SessionExpired : MockGatewayErrors.AlreadyCompleted;
        }

        var final = outcome == MockOutcome.Failed ? MockSessionStatus.Failed : MockSessionStatus.Succeeded;
        var due = outcome == MockOutcome.SucceededDelayed ? now + options.CurrentValue.DelayedWebhookDelay : now;
        var deliveries = BuildDeliveries(session, final, outcome, now);
        if (!await sessions.CompleteAsync(session, final, deliveries, due, now, ct))
        {
            return MockGatewayErrors.AlreadyCompleted;
        }

        return ToView(session with { Status = final }, now);
    }

    private static List<MockDelivery> BuildDeliveries(MockSession session, MockSessionStatus final, MockOutcome outcome, DateTimeOffset now)
    {
        var payload = new MockWebhookPayload(
            $"evt_mock_{Guid.NewGuid():N}",
            final == MockSessionStatus.Failed ? MockWebhookPayload.FailedType : MockWebhookPayload.SucceededType,
            session.SessionId,
            session.Amount.Amount,
            session.Amount.Currency,
            now);
        var body = payload.ToJson();
        var copies = outcome == MockOutcome.SucceededTwice ? 2 : 1;
        return Enumerable.Range(0, copies).Select(_ => new MockDelivery(Guid.NewGuid().ToString("N"), body, 0)).ToList();
    }

    private static MockSessionView ToView(MockSession session, DateTimeOffset now) => new(
        session.SessionId,
        session.OrderId,
        session.EventId,
        session.Label,
        session.Amount.Amount,
        session.Amount.Currency,
        session.EffectiveStatus(now),
        session.ExpiresAt,
        session.ReturnUrl,
        now);
}
