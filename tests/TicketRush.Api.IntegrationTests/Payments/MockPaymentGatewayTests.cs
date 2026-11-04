using StackExchange.Redis;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Application.Abstractions.Payments;
using TicketRush.Domain.Primitives;
using TicketRush.Infrastructure.Payments;

namespace TicketRush.Api.IntegrationTests.Payments;

/// <summary>The mock provider against a real Redis: sessions, idempotency, status, refunds and webhook parsing.</summary>
public sealed class MockPaymentGatewayTests(ContainersFixture containers) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private PaymentHarness _h = null!;

    public async ValueTask InitializeAsync() => _h = await PaymentHarness.CreateAsync(containers);

    public async ValueTask DisposeAsync() => await _h.DisposeAsync();

    private IDatabase Db => _h.Redis.GetDatabase();

    [Fact]
    public async Task Create_redirects_to_the_web_mock_page_and_keeps_the_expiry()
    {
        var session = await _h.CreateSessionAsync(lifetime: TimeSpan.FromMinutes(7));

        session.SessionId.ShouldStartWith("mock_");
        session.RedirectUrl.ToString().ShouldBe($"http://web.test:3000/mock-gateway/{session.SessionId}");
        session.ExpiresAt.ShouldBe(_h.Time.GetUtcNow().AddMinutes(7));
        _h.Gateway.Provider.ShouldBe("mock");
    }

    [Fact]
    public async Task The_same_idempotency_key_gives_the_same_session()
    {
        var order = PaymentHarness.NewOrder();

        var first = await _h.CreateSessionAsync(order, key: "attempt-1");
        var second = await _h.CreateSessionAsync(order, key: "attempt-1");
        var other = await _h.CreateSessionAsync(order, key: "attempt-2");

        second.ShouldBe(first);
        other.SessionId.ShouldNotBe(first.SessionId);
    }

    [Fact]
    public async Task A_double_click_creates_exactly_one_session()
    {
        var order = PaymentHarness.NewOrder();

        var sessions = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => _h.CreateSessionAsync(order, key: "double-click")));

        sessions.Select(s => s.SessionId).Distinct().Count().ShouldBe(1);
        var keys = _h.Redis.GetServer(_h.Redis.GetEndPoints()[0]).Keys(pattern: $"{_h.Keys.Prefix}pay:mock:session:*").ToList();
        keys.Count.ShouldBe(1);
    }

    [Fact]
    public async Task An_idempotency_key_cannot_be_reused_for_another_order()
    {
        await _h.CreateSessionAsync(PaymentHarness.NewOrder(), key: "shared");

        await Should.ThrowAsync<PaymentRejectedException>(() => _h.CreateSessionAsync(PaymentHarness.NewOrder(), key: "shared"));
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(0)]
    public async Task A_session_that_is_already_expired_is_refused(int seconds)
    {
        await Should.ThrowAsync<PaymentRejectedException>(() => _h.CreateSessionAsync(lifetime: TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public async Task The_session_hash_lives_until_expiry_plus_margin()
    {
        var session = await _h.CreateSessionAsync(lifetime: TimeSpan.FromMinutes(7));

        var ttl = await Db.KeyTimeToLiveAsync(_h.Keys.PayMockSession(session.SessionId));

        var expected = TimeSpan.FromMinutes(7) + _h.Options.SessionMargin;
        ttl!.Value.ShouldBeInRange(expected - TimeSpan.FromSeconds(10), expected);
    }

    [Fact]
    public async Task A_succeeded_session_stays_refundable_for_the_retention_period()
    {
        var session = await _h.CreateSessionAsync();
        await _h.Control.CompleteAsync(session.SessionId, MockOutcome.Succeeded, Ct);

        var ttl = await Db.KeyTimeToLiveAsync(_h.Keys.PayMockSession(session.SessionId));

        ttl!.Value.ShouldBeGreaterThan(TimeSpan.FromDays(29));
    }

    [Fact]
    public async Task Status_is_pending_until_the_outcome_is_chosen()
    {
        var session = await _h.CreateSessionAsync();

        (await _h.Gateway.GetStatusAsync(session.SessionId, Ct)).ShouldBe(PaymentStatus.Pending);
    }

    [Theory]
    [InlineData(MockOutcome.Succeeded, PaymentStatus.Succeeded)]
    [InlineData(MockOutcome.Failed, PaymentStatus.Failed)]
    [InlineData(MockOutcome.SucceededTwice, PaymentStatus.Succeeded)]
    public async Task Status_flips_to_the_outcome_when_it_is_chosen(MockOutcome outcome, PaymentStatus expected)
    {
        var session = await _h.CreateSessionAsync();

        (await _h.Control.CompleteAsync(session.SessionId, outcome, Ct)).IsSuccess.ShouldBeTrue();

        (await _h.Gateway.GetStatusAsync(session.SessionId, Ct)).ShouldBe(expected);
    }

    [Fact]
    public async Task A_delayed_webhook_does_not_delay_the_status_the_money_has_moved()
    {
        var session = await _h.CreateSessionAsync();

        await _h.Control.CompleteAsync(session.SessionId, MockOutcome.SucceededDelayed, Ct);

        (await _h.Gateway.GetStatusAsync(session.SessionId, Ct)).ShouldBe(PaymentStatus.Succeeded);
        (await _h.Queue.CountAsync(Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task A_pending_session_reads_as_expired_once_its_time_is_up()
    {
        var session = await _h.CreateSessionAsync(lifetime: TimeSpan.FromMinutes(7));

        _h.Time.Advance(TimeSpan.FromMinutes(7) - TimeSpan.FromMilliseconds(1));
        (await _h.Gateway.GetStatusAsync(session.SessionId, Ct)).ShouldBe(PaymentStatus.Pending);
        _h.Time.Advance(TimeSpan.FromMilliseconds(1));

        (await _h.Gateway.GetStatusAsync(session.SessionId, Ct)).ShouldBe(PaymentStatus.Expired);
    }

    [Fact]
    public async Task An_unknown_session_reads_as_expired()
    {
        (await _h.Gateway.GetStatusAsync("mock_nope", Ct)).ShouldBe(PaymentStatus.Expired);
    }

    [Fact]
    public async Task A_succeeded_session_stays_succeeded_after_its_expiry()
    {
        var session = await _h.CreateSessionAsync(lifetime: TimeSpan.FromMinutes(7));
        await _h.Control.CompleteAsync(session.SessionId, MockOutcome.Succeeded, Ct);

        _h.Time.Advance(TimeSpan.FromMinutes(30));

        (await _h.Gateway.GetStatusAsync(session.SessionId, Ct)).ShouldBe(PaymentStatus.Succeeded);
    }

    [Fact]
    public async Task Refund_is_idempotent_per_key()
    {
        var session = await _h.CreateSessionAsync(PaymentHarness.NewOrder(500_000, 2));
        await _h.Control.CompleteAsync(session.SessionId, MockOutcome.Succeeded, Ct);

        var first = await _h.Gateway.RefundAsync(session.SessionId, new Money(1_000_000), "order-1", Ct);
        var again = await _h.Gateway.RefundAsync(session.SessionId, new Money(1_000_000), "order-1", Ct);

        first.IsSuccess.ShouldBeTrue();
        again.ShouldBe(first);
        (await Db.HashGetAsync(_h.Keys.PayMockSession(session.SessionId), "refunded")).ToString().ShouldBe("1000000");
    }

    [Fact]
    public async Task Concurrent_refunds_with_one_key_book_the_amount_once()
    {
        var session = await _h.CreateSessionAsync(PaymentHarness.NewOrder(500_000, 2));
        await _h.Control.CompleteAsync(session.SessionId, MockOutcome.Succeeded, Ct);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            _h.Gateway.RefundAsync(session.SessionId, new Money(400_000), "same-key", Ct)));

        results.Select(r => r.RefundId).Distinct().Count().ShouldBe(1);
        (await Db.HashGetAsync(_h.Keys.PayMockSession(session.SessionId), "refunded")).ToString().ShouldBe("400000");
    }

    [Fact]
    public async Task Refunds_cannot_exceed_what_was_paid()
    {
        var session = await _h.CreateSessionAsync(PaymentHarness.NewOrder(500_000, 2));
        await _h.Control.CompleteAsync(session.SessionId, MockOutcome.Succeeded, Ct);
        (await _h.Gateway.RefundAsync(session.SessionId, new Money(700_000), "part-1", Ct)).IsSuccess.ShouldBeTrue();

        var tooMuch = await _h.Gateway.RefundAsync(session.SessionId, new Money(400_000), "part-2", Ct);
        var rest = await _h.Gateway.RefundAsync(session.SessionId, new Money(300_000), "part-3", Ct);

        tooMuch.Status.ShouldBe(RefundStatus.Failed);
        tooMuch.FailureReason.ShouldBe("amount_exceeds_payment");
        rest.IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData(false, "payment_not_succeeded")]
    [InlineData(true, "session_not_found")]
    public async Task Refunds_of_unpaid_or_unknown_sessions_fail_without_being_remembered(bool unknown, string reason)
    {
        var session = await _h.CreateSessionAsync();
        var id = unknown ? "mock_nope" : session.SessionId;

        var result = await _h.Gateway.RefundAsync(id, new Money(1000), "k", Ct);

        result.Status.ShouldBe(RefundStatus.Failed);
        result.FailureReason.ShouldBe(reason);
        (await Db.KeyExistsAsync(_h.Keys.PayMockRefund("k"))).ShouldBeFalse();
    }

    [Fact]
    public async Task A_cancelled_token_stops_every_mock_call_before_it_touches_redis()
    {
        var session = await _h.CreateSessionAsync();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var order = PaymentHarness.NewOrder();
        var sessionKeysBefore = _h.Redis.GetServer(_h.Redis.GetEndPoints()[0]).Keys(pattern: $"{_h.Keys.Prefix}pay:mock:session:*").Count();

        await Should.ThrowAsync<OperationCanceledException>(() => _h.Gateway.CreateSessionAsync(
            order, new Uri("http://web.test:3000/r"), _h.Time.GetUtcNow().AddMinutes(5), "cancelled-key", cancelled.Token));
        await Should.ThrowAsync<OperationCanceledException>(() => _h.Gateway.GetStatusAsync(session.SessionId, cancelled.Token));
        await Should.ThrowAsync<OperationCanceledException>(() => _h.Gateway.RefundAsync(session.SessionId, new Money(1), "k", cancelled.Token));
        await Should.ThrowAsync<OperationCanceledException>(() => _h.Control.CompleteAsync(session.SessionId, MockOutcome.Succeeded, cancelled.Token));
        await Should.ThrowAsync<OperationCanceledException>(() => _h.Control.GetAsync(session.SessionId, cancelled.Token));
        await Should.ThrowAsync<OperationCanceledException>(() => _h.Queue.CountAsync(cancelled.Token));
        await Should.ThrowAsync<OperationCanceledException>(() => _h.Deliverer.RunOnceAsync(cancelled.Token));

        _h.Redis.GetServer(_h.Redis.GetEndPoints()[0]).Keys(pattern: $"{_h.Keys.Prefix}pay:mock:session:*").Count().ShouldBe(sessionKeysBefore);
        (await Db.KeyExistsAsync(_h.Keys.PayMockIdempotency("cancelled-key"))).ShouldBeFalse();
        (await _h.Gateway.GetStatusAsync(session.SessionId, Ct)).ShouldBe(PaymentStatus.Pending);
    }

    [Fact]
    public async Task Through_the_resilience_pipeline_a_cancelled_mock_call_is_not_retried_and_not_counted()
    {
        var options = new GatewayResilienceOptions { RetryBaseDelay = TimeSpan.Zero, BreakerMinimumThroughput = 2 };
        var resilience = new GatewayResilience("mock", options, _h.Time, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        var gateway = new ResilientPaymentGateway(_h.Gateway, resilience);
        _h.Gateway.SimulateOutage = true; // would surface as "unavailable" (and be counted) if the call ever got past the token check
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        for (var i = 0; i < 5; i++)
        {
            await Should.ThrowAsync<OperationCanceledException>(() => gateway.GetStatusAsync("mock_x", cancelled.Token));
        }

        resilience.CircuitState.ShouldBe(Polly.CircuitBreaker.CircuitState.Closed);
        await Should.ThrowAsync<PaymentGatewayUnavailableException>(() => gateway.GetStatusAsync("mock_x", Ct)); // a live token still sees the outage
    }

    [Fact]
    public async Task Simulated_outage_fails_create_status_and_refund_but_not_verification()
    {
        var session = await _h.CreateSessionAsync();
        _h.Gateway.SimulateOutage = true;

        await Should.ThrowAsync<HttpRequestException>(() => _h.CreateSessionAsync());
        await Should.ThrowAsync<HttpRequestException>(() => _h.Gateway.GetStatusAsync(session.SessionId, Ct));
        await Should.ThrowAsync<HttpRequestException>(() => _h.Gateway.RefundAsync(session.SessionId, new Money(1), "k", Ct));
        Should.Throw<InvalidWebhookSignatureException>(() => _h.Gateway.VerifyAndParse(PaymentHarness.Headers(null), "{}"));

        _h.Gateway.SimulateOutage = false;
        (await _h.Gateway.GetStatusAsync(session.SessionId, Ct)).ShouldBe(PaymentStatus.Pending);
    }

    [Fact]
    public async Task VerifyAndParse_returns_the_event_of_a_signed_webhook()
    {
        var session = await _h.CreateSessionAsync(PaymentHarness.NewOrder(500_000, 2));
        await _h.Control.CompleteAsync(session.SessionId, MockOutcome.Succeeded, Ct);
        var body = (await _h.Queue.ClaimDueAsync(_h.Time.GetUtcNow(), 10, TimeSpan.FromSeconds(30), Ct)).Single().Delivery.Body;
        var signature = WebhookSignature.Sign(PaymentHarness.Secret, body, _h.Time.GetUtcNow());

        var webhook = _h.Gateway.VerifyAndParse(PaymentHarness.Headers(signature), body);

        webhook.SessionId.ShouldBe(session.SessionId);
        webhook.Type.ShouldBe(WebhookEventType.Succeeded);
        webhook.Amount.ShouldBe(new Money(1_000_000));
        webhook.ProviderEventId.ShouldStartWith("evt_mock_");
        webhook.OccurredAt.ShouldBe(_h.Time.GetUtcNow());
    }

    [Fact]
    public async Task A_gateway_without_a_secret_never_accepts_a_webhook_even_one_signed_with_an_empty_key()
    {
        await _h.DisposeAsync();
        _h = await PaymentHarness.CreateAsync(containers, o => o.WebhookSecret = string.Empty);
        const string body = """{"id":"e","type":"payment.succeeded","sessionId":"s","occurredAt":"2026-11-04T10:00:00Z"}""";
        var now = _h.Time.GetUtcNow().ToUnixTimeSeconds();
        var forged = $"t={now},v1={Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData(Array.Empty<byte>(), System.Text.Encoding.UTF8.GetBytes($"{now}.{body}")))}";

        Should.Throw<ArgumentException>(() => _h.Gateway.VerifyAndParse(PaymentHarness.Headers(forged), body));
    }

    [Fact]
    public void VerifyAndParse_rejects_a_stale_or_unsigned_or_forged_webhook()
    {
        const string body = """{"id":"e","type":"payment.succeeded","sessionId":"s","occurredAt":"2026-11-04T10:00:00Z"}""";
        var now = _h.Time.GetUtcNow();

        Should.Throw<InvalidWebhookSignatureException>(() =>
            _h.Gateway.VerifyAndParse(PaymentHarness.Headers(WebhookSignature.Sign(PaymentHarness.Secret, body, now.AddMinutes(-5).AddSeconds(-1))), body));
        Should.Throw<InvalidWebhookSignatureException>(() =>
            _h.Gateway.VerifyAndParse(PaymentHarness.Headers(WebhookSignature.Sign(PaymentHarness.Secret, body, now.AddMinutes(5).AddSeconds(1))), body));
        Should.Throw<InvalidWebhookSignatureException>(() => _h.Gateway.VerifyAndParse(PaymentHarness.Headers(null), body));
        Should.Throw<InvalidWebhookSignatureException>(() =>
            _h.Gateway.VerifyAndParse(PaymentHarness.Headers(WebhookSignature.Sign("another-secret-0123456789", body, now)), body));
        Should.NotThrow(() => _h.Gateway.VerifyAndParse(PaymentHarness.Headers(WebhookSignature.Sign(PaymentHarness.Secret, body, now)), body));
    }

    [Fact]
    public void The_tolerance_follows_the_clock_it_is_given()
    {
        const string body = """{"id":"e","type":"payment.failed","sessionId":"s","occurredAt":"2026-11-04T10:00:00Z"}""";
        var signature = WebhookSignature.Sign(PaymentHarness.Secret, body, _h.Time.GetUtcNow());

        _h.Time.Advance(TimeSpan.FromMinutes(5));
        Should.NotThrow(() => _h.Gateway.VerifyAndParse(PaymentHarness.Headers(signature), body));
        _h.Time.Advance(TimeSpan.FromSeconds(1));

        Should.Throw<InvalidWebhookSignatureException>(() => _h.Gateway.VerifyAndParse(PaymentHarness.Headers(signature), body));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"id":"e","type":"payment.exploded","sessionId":"s","occurredAt":"2026-11-04T10:00:00Z"}""")]
    [InlineData("""{"id":"e","type":"payment.succeeded","sessionId":"s","amount":-5,"occurredAt":"2026-11-04T10:00:00Z"}""")]
    public void A_signed_body_that_is_not_an_event_is_a_payload_error_not_a_signature_error(string body)
    {
        var signature = WebhookSignature.Sign(PaymentHarness.Secret, body, _h.Time.GetUtcNow());

        Should.Throw<InvalidWebhookPayloadException>(() => _h.Gateway.VerifyAndParse(PaymentHarness.Headers(signature), body));
    }

    [Fact]
    public async Task Completing_twice_or_after_expiry_is_refused()
    {
        var session = await _h.CreateSessionAsync(lifetime: TimeSpan.FromMinutes(1));
        var late = await _h.CreateSessionAsync(lifetime: TimeSpan.FromMinutes(1));
        (await _h.Control.CompleteAsync(session.SessionId, MockOutcome.Succeeded, Ct)).IsSuccess.ShouldBeTrue();
        _h.Time.Advance(TimeSpan.FromMinutes(2));

        (await _h.Control.CompleteAsync(session.SessionId, MockOutcome.Failed, Ct)).Error.ShouldBe(MockGatewayErrors.AlreadyCompleted);
        (await _h.Control.CompleteAsync(late.SessionId, MockOutcome.Succeeded, Ct)).Error.ShouldBe(MockGatewayErrors.SessionExpired);
        (await _h.Control.CompleteAsync("mock_nope", MockOutcome.Succeeded, Ct)).Error.ShouldBe(MockGatewayErrors.SessionNotFound);
        (await _h.Queue.CountAsync(Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Racing_completions_pick_one_winner_and_queue_one_webhook()
    {
        var session = await _h.CreateSessionAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(i =>
            _h.Control.CompleteAsync(session.SessionId, i % 2 == 0 ? MockOutcome.Succeeded : MockOutcome.Failed, Ct)));

        results.Count(r => r.IsSuccess).ShouldBe(1);
        (await _h.Queue.CountAsync(Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task The_session_view_carries_what_the_page_shows()
    {
        var order = PaymentHarness.NewOrder(500_000, 2);
        var session = await _h.CreateSessionAsync(order);

        var view = (await _h.Control.GetAsync(session.SessionId, Ct))!;

        view.Amount.ShouldBe(1_000_000);
        view.Currency.ShouldBe("VND");
        view.OrderId.ShouldBe(order.Id);
        view.Label.ShouldStartWith("Đơn hàng ");
        view.Status.ShouldBe(PaymentStatus.Pending);
        view.ExpiresAt.ShouldBe(session.ExpiresAt);
        view.ReturnUrl.ToString().ShouldBe("http://web.test:3000/checkout/result");
        view.ServerNow.ShouldBe(_h.Time.GetUtcNow());
        (await _h.Control.GetAsync("mock_nope", Ct)).ShouldBeNull();
    }
}
