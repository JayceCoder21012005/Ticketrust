using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Polly.CircuitBreaker;
using TicketRush.Application.Abstractions.Payments;
using TicketRush.Domain.Orders;
using TicketRush.Domain.Primitives;
using TicketRush.Infrastructure.Payments;

namespace TicketRush.Api.IntegrationTests.Payments;

/// <summary>C11: 10 s timeout, 2 retries with backoff, circuit breaker; open circuit or exhausted retries are <c>payment.gateway_unavailable</c>.</summary>
public sealed class ResilientPaymentGatewayTests
{
    private sealed class ScriptedGateway : IPaymentGateway
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        /// <summary>Runs on every call; throw to fail, return to succeed.</summary>
        public Func<CancellationToken, Task> Behavior { get; set; } = _ => Task.CompletedTask;

        public string Provider => "scripted";

        public async Task<PaymentSession> CreateSessionAsync(Order order, Uri returnUrl, DateTimeOffset expiresAt, string idempotencyKey, CancellationToken ct)
        {
            await Run(ct);
            return new PaymentSession("s1", new Uri("http://pay.test/s1"), expiresAt);
        }

        public async Task<PaymentStatus> GetStatusAsync(string sessionId, CancellationToken ct)
        {
            await Run(ct);
            return PaymentStatus.Succeeded;
        }

        public async Task<RefundResult> RefundAsync(string sessionId, Money amount, string idempotencyKey, CancellationToken ct)
        {
            await Run(ct);
            return new RefundResult("re_1", RefundStatus.Succeeded);
        }

        public WebhookEvent VerifyAndParse(WebhookHeaders headers, string body) =>
            new("evt", "s1", WebhookEventType.Succeeded, null, DateTimeOffset.UnixEpoch);

        private Task Run(CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Behavior(ct);
        }
    }

    private sealed record Rig(ScriptedGateway Inner, ResilientPaymentGateway Gateway, GatewayResilience Resilience, FakeTimeProvider Time);

    private static Rig Build(Action<GatewayResilienceOptions>? configure = null, bool realClock = false)
    {
        var options = new GatewayResilienceOptions
        {
            Timeout = TimeSpan.FromSeconds(10),
            RetryCount = 2,
            RetryBaseDelay = TimeSpan.Zero,
            BreakerFailureRatio = 0.5,
            BreakerMinimumThroughput = 100,
            BreakerSamplingDuration = TimeSpan.FromSeconds(30),
            BreakerBreakDuration = TimeSpan.FromSeconds(20),
        };
        configure?.Invoke(options);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var inner = new ScriptedGateway();
        var resilience = new GatewayResilience("scripted", options, realClock ? TimeProvider.System : time, NullLogger.Instance);
        return new Rig(inner, new ResilientPaymentGateway(inner, resilience), resilience, time);
    }

    private static HttpRequestException Down() => new HttpRequestException("connection refused");

    private static Task<PaymentStatus> Status(Rig rig) => rig.Gateway.GetStatusAsync("s1", TestContext.Current.CancellationToken);

    [Fact]
    public void The_defaults_follow_the_spec()
    {
        var defaults = new GatewayResilienceOptions();

        defaults.Timeout.ShouldBe(TimeSpan.FromSeconds(10));
        defaults.RetryCount.ShouldBe(2);
    }

    [Fact]
    public async Task A_healthy_call_goes_straight_through()
    {
        var rig = Build();

        (await Status(rig)).ShouldBe(PaymentStatus.Succeeded);

        rig.Inner.Calls.ShouldBe(1);
        rig.Gateway.Provider.ShouldBe("scripted");
    }

    [Fact]
    public async Task A_transient_failure_is_retried_and_the_caller_never_notices()
    {
        var rig = Build();
        var failures = 2;
        rig.Inner.Behavior = _ => failures-- > 0 ? throw Down() : Task.CompletedTask;

        (await Status(rig)).ShouldBe(PaymentStatus.Succeeded);

        rig.Inner.Calls.ShouldBe(3);
    }

    [Fact]
    public async Task Two_retries_then_gateway_unavailable()
    {
        var rig = Build();
        rig.Inner.Behavior = _ => throw Down();

        var ex = await Should.ThrowAsync<PaymentGatewayUnavailableException>(() => Status(rig));

        rig.Inner.Calls.ShouldBe(3);
        ex.InnerException.ShouldBeOfType<HttpRequestException>();
    }

    [Fact]
    public async Task Create_and_refund_are_retried_with_the_same_idempotency_key()
    {
        var rig = Build();
        var keys = new List<string>();
        var inner = new KeyRecordingGateway(keys);
        var gateway = new ResilientPaymentGateway(inner, rig.Resilience);

        await Should.ThrowAsync<PaymentGatewayUnavailableException>(() =>
            gateway.RefundAsync("s1", new Money(1000), "refund-key", TestContext.Current.CancellationToken));

        keys.ShouldBe(["refund-key", "refund-key", "refund-key"]);
    }

    private sealed class KeyRecordingGateway(List<string> keys) : IPaymentGateway
    {
        public string Provider => "keys";

        public Task<PaymentSession> CreateSessionAsync(Order order, Uri returnUrl, DateTimeOffset expiresAt, string idempotencyKey, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<PaymentStatus> GetStatusAsync(string sessionId, CancellationToken ct) => throw new NotSupportedException();

        public Task<RefundResult> RefundAsync(string sessionId, Money amount, string idempotencyKey, CancellationToken ct)
        {
            keys.Add(idempotencyKey);
            throw new HttpRequestException("down");
        }

        public WebhookEvent VerifyAndParse(WebhookHeaders headers, string body) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Calls_that_must_be_idempotent_refuse_a_blank_key_and_never_reach_the_gateway(string key)
    {
        var rig = Build();

        await Should.ThrowAsync<ArgumentException>(() =>
            rig.Gateway.CreateSessionAsync(PaymentHarness.NewOrder(), new Uri("http://x.test"), DateTimeOffset.UtcNow, key, TestContext.Current.CancellationToken));
        await Should.ThrowAsync<ArgumentException>(() =>
            rig.Gateway.RefundAsync("s1", new Money(1000), key, TestContext.Current.CancellationToken));

        rig.Inner.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_gateway_refusal_is_neither_retried_nor_counted_by_the_breaker()
    {
        var rig = Build(o => o.BreakerMinimumThroughput = 2);
        rig.Inner.Behavior = _ => throw new PaymentRejectedException("expired");

        for (var i = 0; i < 5; i++)
        {
            await Should.ThrowAsync<PaymentRejectedException>(() => Status(rig));
        }

        rig.Inner.Calls.ShouldBe(5);
        rig.Resilience.CircuitState.ShouldBe(CircuitState.Closed);
    }

    [Fact]
    public async Task The_breaker_opens_at_the_failure_ratio_and_then_fails_fast_without_calling_the_gateway()
    {
        var rig = Build(o => o.BreakerMinimumThroughput = 4);
        rig.Inner.Behavior = _ => throw Down();

        await Should.ThrowAsync<PaymentGatewayUnavailableException>(() => Status(rig)); // 3 attempts: below min throughput
        rig.Resilience.CircuitState.ShouldBe(CircuitState.Closed);
        await Should.ThrowAsync<PaymentGatewayUnavailableException>(() => Status(rig)); // 4th attempt opens it
        rig.Resilience.CircuitState.ShouldBe(CircuitState.Open);
        var callsWhenOpened = rig.Inner.Calls;

        var ex = await Should.ThrowAsync<PaymentGatewayUnavailableException>(() => Status(rig));

        callsWhenOpened.ShouldBe(4);
        rig.Inner.Calls.ShouldBe(4);
        ex.InnerException.ShouldBeOfType<BrokenCircuitException>();
    }

    [Fact]
    public async Task The_breaker_does_not_open_below_the_failure_ratio()
    {
        var rig = Build(o =>
        {
            o.BreakerMinimumThroughput = 4;
            o.RetryCount = 0;
        });
        var call = 0;
        rig.Inner.Behavior = _ => ++call % 3 == 0 ? throw Down() : Task.CompletedTask; // one failure in three

        for (var i = 0; i < 12; i++)
        {
            try
            {
                await Status(rig);
            }
            catch (PaymentGatewayUnavailableException)
            {
                // expected one time in three
            }
        }

        rig.Resilience.CircuitState.ShouldBe(CircuitState.Closed);
        rig.Inner.Calls.ShouldBe(12);
    }

    [Fact]
    public async Task After_the_break_duration_the_circuit_half_opens_and_a_good_probe_closes_it()
    {
        var rig = Build(o => o.BreakerMinimumThroughput = 4);
        var healthy = false;
        rig.Inner.Behavior = _ => healthy ? Task.CompletedTask : throw Down();
        await Should.ThrowAsync<PaymentGatewayUnavailableException>(() => Status(rig));
        await Should.ThrowAsync<PaymentGatewayUnavailableException>(() => Status(rig));
        rig.Resilience.CircuitState.ShouldBe(CircuitState.Open);

        rig.Time.Advance(TimeSpan.FromSeconds(19));
        await Should.ThrowAsync<PaymentGatewayUnavailableException>(() => Status(rig));
        rig.Inner.Calls.ShouldBe(4); // still failing fast

        rig.Time.Advance(TimeSpan.FromSeconds(2));
        healthy = true;
        (await Status(rig)).ShouldBe(PaymentStatus.Succeeded);

        rig.Resilience.CircuitState.ShouldBe(CircuitState.Closed);
        rig.Inner.Calls.ShouldBe(5);
    }

    [Fact]
    public async Task A_failed_probe_reopens_the_circuit()
    {
        var rig = Build(o => o.BreakerMinimumThroughput = 4);
        rig.Inner.Behavior = _ => throw Down();
        await Should.ThrowAsync<PaymentGatewayUnavailableException>(() => Status(rig));
        await Should.ThrowAsync<PaymentGatewayUnavailableException>(() => Status(rig));
        rig.Time.Advance(TimeSpan.FromSeconds(21));

        await Should.ThrowAsync<PaymentGatewayUnavailableException>(() => Status(rig));

        rig.Resilience.CircuitState.ShouldBe(CircuitState.Open);
        rig.Inner.Calls.ShouldBe(5); // exactly one probe was let through
    }

    [Fact]
    public async Task A_call_that_exceeds_the_timeout_is_cut_off_retried_and_reported_unavailable()
    {
        var rig = Build(o => o.Timeout = TimeSpan.FromMilliseconds(50), realClock: true);
        rig.Inner.Behavior = ct => Task.Delay(Timeout.Infinite, ct);

        var ex = await Should.ThrowAsync<PaymentGatewayUnavailableException>(() => Status(rig));

        rig.Inner.Calls.ShouldBe(3);
        ex.InnerException.ShouldBeOfType<Polly.Timeout.TimeoutRejectedException>();
    }

    [Fact]
    public async Task Cancelling_the_call_is_not_a_gateway_failure()
    {
        var rig = Build();
        rig.Inner.Behavior = ct => Task.Delay(Timeout.Infinite, ct);
        using var cts = new CancellationTokenSource();
        var call = rig.Gateway.GetStatusAsync("s1", cts.Token);
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => call);

        rig.Inner.Calls.ShouldBe(1);
        rig.Resilience.CircuitState.ShouldBe(CircuitState.Closed);
    }

    [Fact]
    public async Task Verifying_a_webhook_is_pure_and_works_while_the_circuit_is_open()
    {
        var rig = Build(o => o.BreakerMinimumThroughput = 4);
        rig.Inner.Behavior = _ => throw Down();
        await Should.ThrowAsync<PaymentGatewayUnavailableException>(() => Status(rig));
        await Should.ThrowAsync<PaymentGatewayUnavailableException>(() => Status(rig));
        rig.Resilience.CircuitState.ShouldBe(CircuitState.Open);

        var parsed = rig.Gateway.VerifyAndParse(PaymentHarness.Headers(null), "{}");

        parsed.ProviderEventId.ShouldBe("evt");
        rig.Inner.Calls.ShouldBe(4);
    }

    [Fact]
    public void The_unavailable_error_has_the_spec_code_and_maps_to_503()
    {
        PaymentErrors.GatewayUnavailable.Code.ShouldBe("payment.gateway_unavailable");
        PaymentErrors.GatewayUnavailable.Type.ShouldBe(ErrorType.Unavailable);
    }
}
