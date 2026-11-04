extern alias WorkerAssembly;

using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Application.Abstractions.Payments;
using TicketRush.Infrastructure.Locking;
using TicketRush.Infrastructure.Payments;
using WorkerAssembly::TicketRush.Worker.Payments;

namespace TicketRush.Api.IntegrationTests.Payments;

/// <summary>The mock gateway's webhook outbox and sender: due times, duplicates, signing and retries.</summary>
public sealed class MockWebhookDeliveryTests(ContainersFixture containers) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private PaymentHarness _h = null!;

    public async ValueTask InitializeAsync() => _h = await PaymentHarness.CreateAsync(containers);

    public async ValueTask DisposeAsync() => await _h.DisposeAsync();

    private async Task<string> CompleteAsync(MockOutcome outcome)
    {
        var session = await _h.CreateSessionAsync();
        (await _h.Control.CompleteAsync(session.SessionId, outcome)).IsSuccess.ShouldBeTrue();
        return session.SessionId;
    }

    private WebhookEvent Verify(RecordingHandler.Received received) =>
        _h.Gateway.VerifyAndParse(PaymentHarness.Headers(received.Signature), received.Body);

    [Fact]
    public async Task A_succeeded_payment_is_delivered_signed_to_the_webhook_url()
    {
        var sessionId = await CompleteAsync(MockOutcome.Succeeded);

        (await _h.Deliverer.RunOnceAsync(Ct)).ShouldBe(1);

        var received = _h.Handler.Requests.Single();
        received.Url.ToString().ShouldBe("http://api.test/api/v1/webhooks/payments/mock");
        received.Signature.ShouldNotBeNull().ShouldStartWith($"t={_h.Time.GetUtcNow().ToUnixTimeSeconds()},v1=");
        var webhook = Verify(received);
        webhook.SessionId.ShouldBe(sessionId);
        webhook.Type.ShouldBe(WebhookEventType.Succeeded);
        (await _h.Queue.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task A_failed_payment_is_delivered_as_a_failed_event()
    {
        await CompleteAsync(MockOutcome.Failed);

        await _h.Deliverer.RunOnceAsync(Ct);

        Verify(_h.Handler.Requests.Single()).Type.ShouldBe(WebhookEventType.Failed);
    }

    [Fact]
    public async Task Nothing_is_sent_when_nothing_is_due()
    {
        (await _h.Deliverer.RunOnceAsync(Ct)).ShouldBe(0);

        _h.Handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_delayed_webhook_is_not_sent_before_its_due_time()
    {
        await CompleteAsync(MockOutcome.SucceededDelayed);

        await _h.Deliverer.RunOnceAsync(Ct);
        _h.Time.Advance(TimeSpan.FromMinutes(3) - TimeSpan.FromSeconds(1));
        await _h.Deliverer.RunOnceAsync(Ct);

        _h.Handler.Requests.ShouldBeEmpty();
        (await _h.Queue.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task A_delayed_webhook_is_sent_after_three_minutes_with_a_fresh_signature()
    {
        await CompleteAsync(MockOutcome.SucceededDelayed);

        _h.Time.Advance(TimeSpan.FromMinutes(3));
        (await _h.Deliverer.RunOnceAsync(Ct)).ShouldBe(1);

        var received = _h.Handler.Requests.Single();
        received.Signature.ShouldNotBeNull().ShouldStartWith($"t={_h.Time.GetUtcNow().ToUnixTimeSeconds()},");
        Should.NotThrow(() => Verify(received));
    }

    [Fact]
    public async Task The_delay_is_an_option()
    {
        await _h.DisposeAsync();
        _h = await PaymentHarness.CreateAsync(containers, o => o.DelayedWebhookDelay = TimeSpan.FromSeconds(20));
        await CompleteAsync(MockOutcome.SucceededDelayed);

        _h.Time.Advance(TimeSpan.FromSeconds(19));
        (await _h.Deliverer.RunOnceAsync(Ct)).ShouldBe(0);
        _h.Time.Advance(TimeSpan.FromSeconds(1));
        (await _h.Deliverer.RunOnceAsync(Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Succeeded_twice_sends_two_identical_bodies()
    {
        await CompleteAsync(MockOutcome.SucceededTwice);

        (await _h.Deliverer.RunOnceAsync(Ct)).ShouldBe(2);

        var requests = _h.Handler.Requests;
        requests.Count.ShouldBe(2);
        requests[0].Body.ShouldBe(requests[1].Body);
        Verify(requests[0]).ProviderEventId.ShouldBe(Verify(requests[1]).ProviderEventId);
    }

    [Fact]
    public async Task A_server_error_is_retried_with_exponential_backoff()
    {
        await CompleteAsync(MockOutcome.Succeeded);
        _h.Handler.Script.Enqueue(HttpStatusCode.InternalServerError);
        _h.Handler.Script.Enqueue(HttpStatusCode.InternalServerError);
        _h.Handler.Script.Enqueue(HttpStatusCode.OK);

        (await _h.Deliverer.RunOnceAsync(Ct)).ShouldBe(0); // attempt 1 fails, next in 2 s
        (await _h.Deliverer.RunOnceAsync(Ct)).ShouldBe(0); // not due yet
        _h.Handler.Requests.Count.ShouldBe(1);

        _h.Time.Advance(TimeSpan.FromSeconds(2));
        (await _h.Deliverer.RunOnceAsync(Ct)).ShouldBe(0); // attempt 2 fails, next in 4 s
        _h.Time.Advance(TimeSpan.FromSeconds(3));
        (await _h.Deliverer.RunOnceAsync(Ct)).ShouldBe(0);
        _h.Handler.Requests.Count.ShouldBe(2);

        _h.Time.Advance(TimeSpan.FromSeconds(1));
        (await _h.Deliverer.RunOnceAsync(Ct)).ShouldBe(1); // attempt 3 succeeds

        _h.Handler.Requests.Count.ShouldBe(3);
        _h.Handler.Requests.Select(r => r.Body).Distinct().Count().ShouldBe(1);
        (await _h.Queue.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Every_retry_is_signed_with_the_time_it_is_sent_so_late_retries_stay_inside_the_replay_window()
    {
        await CompleteAsync(MockOutcome.Succeeded);
        _h.Handler.Script.Enqueue(HttpStatusCode.ServiceUnavailable);
        _h.Handler.Script.Enqueue(HttpStatusCode.OK);
        await _h.Deliverer.RunOnceAsync(Ct);

        _h.Time.Advance(TimeSpan.FromSeconds(2));
        await _h.Deliverer.RunOnceAsync(Ct);

        var signatures = _h.Handler.Requests.Select(r => r.Signature).ToList();
        signatures[0].ShouldNotBe(signatures[1]);
        Should.NotThrow(() => Verify(_h.Handler.Requests[1]));
    }

    [Fact]
    public async Task A_missing_endpoint_or_unreachable_api_is_retried_quietly()
    {
        await CompleteAsync(MockOutcome.Succeeded);
        _h.Handler.Script.Enqueue(HttpStatusCode.NotFound);
        await _h.Deliverer.RunOnceAsync(Ct);
        _h.Handler.Throw = new HttpRequestException("connection refused");
        _h.Time.Advance(TimeSpan.FromSeconds(2));

        (await _h.Deliverer.RunOnceAsync(Ct)).ShouldBe(0);

        (await _h.Queue.CountAsync()).ShouldBe(1);
        _h.Handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_delivery_is_dropped_after_the_attempt_limit()
    {
        await _h.DisposeAsync();
        _h = await PaymentHarness.CreateAsync(containers, o => o.MaxDeliveryAttempts = 2);
        await CompleteAsync(MockOutcome.Succeeded);
        _h.Handler.Script.Enqueue(HttpStatusCode.InternalServerError);

        await _h.Deliverer.RunOnceAsync(Ct);
        _h.Time.Advance(TimeSpan.FromSeconds(2));
        await _h.Deliverer.RunOnceAsync(Ct);

        _h.Handler.Requests.Count.ShouldBe(2);
        (await _h.Queue.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task A_claimed_delivery_is_leased_and_comes_back_if_the_sender_dies()
    {
        await CompleteAsync(MockOutcome.Succeeded);
        var now = _h.Time.GetUtcNow();

        (await _h.Queue.ClaimDueAsync(now, 10, TimeSpan.FromSeconds(30))).Count.ShouldBe(1);
        (await _h.Queue.ClaimDueAsync(now.AddSeconds(29), 10, TimeSpan.FromSeconds(30))).ShouldBeEmpty();
        (await _h.Queue.ClaimDueAsync(now.AddSeconds(30), 10, TimeSpan.FromSeconds(30))).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_garbage_entry_in_the_queue_is_discarded()
    {
        await _h.Redis.GetDatabase().SortedSetAddAsync(_h.Keys.PayMockDeliveries, "not json", 0);

        (await _h.Deliverer.RunOnceAsync(Ct)).ShouldBe(0);

        (await _h.Queue.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task The_worker_service_delivers_as_leader()
    {
        await CompleteAsync(MockOutcome.Succeeded);
        var distributedLock = new RedisDistributedLock(_h.Redis, _h.Keys, NullLogger<RedisDistributedLock>.Instance);
        var service = new MockWebhookDeliveryService(_h.Deliverer, distributedLock, TimeProvider.System, NullLogger<MockWebhookDeliveryService>.Instance);

        await service.StartAsync(Ct);
        try
        {
            await WaitUntilAsync(() => _h.Handler.Requests.Count == 1);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        (await _h.Queue.CountAsync()).ShouldBe(0);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition not met in 10 s.");
            }

            await Task.Delay(50, Ct);
        }
    }
}
