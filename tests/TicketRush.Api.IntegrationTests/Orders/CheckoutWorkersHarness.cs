extern alias WorkerAssembly;

using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Application.Abstractions.Inventory;
using TicketRush.Application.Abstractions.Locking;
using TicketRush.Application.Abstractions.Payments;
using TicketRush.Application.Features.Orders;
using TicketRush.Application.Options;
using TicketRush.Domain.Events;
using TicketRush.Domain.Orders;
using TicketRush.Domain.Primitives;
using TicketRush.Domain.Tickets;
using TicketRush.Infrastructure.Inventory;
using TicketRush.Infrastructure.Payments;
using TicketRush.Infrastructure.Persistence;
using TicketRush.Infrastructure.Persistence.Seed;
using WorkerAssembly::TicketRush.Worker.Payments;
using Order = TicketRush.Domain.Orders.Order;

namespace TicketRush.Api.IntegrationTests.Orders;

/// <summary>
/// The api's real services (Postgres, Redis, the real <see cref="MockPaymentGateway"/>, the real fulfil path) on a fake
/// clock, plus the worker's <see cref="OrderExpiryService"/> and <see cref="RefundWorker"/> built over them. Every gateway
/// call the workers make goes through <see cref="GatewaySpy"/>, which counts calls and can run a hook around them.
/// </summary>
internal sealed class CheckoutWorkersHarness : IAsyncDisposable
{
    public const string Provider = MockPaymentGateway.ProviderName;

    private readonly ApiFactory _factory;

    private CheckoutWorkersHarness(ApiFactory factory, FakeTimeProvider time, GatewaySpy spy)
    {
        _factory = factory;
        Time = time;
        Spy = spy;
        var gate = SaleLocks;
        Host = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.AddSingleton<TimeProvider>(time);
            s.AddScoped<IEventSaleLock>(sp => new GatedSaleLock(ActivatorUtilities.CreateInstance<EventSaleLock>(sp), gate));
            s.AddSingleton<IPaymentGatewayResolver>(sp => new SpyResolver(ActivatorUtilities.CreateInstance<PaymentGatewayResolver>(sp), spy));
            s.AddScoped<IWebhookInbox>(sp => new FaultyInbox(ActivatorUtilities.CreateInstance<WebhookInbox>(sp), spy));
        }));
        Metrics = new PaymentWorkerMetrics(Host.Services.GetRequiredService<IMeterFactory>());
    }

    public sealed record Placed(Guid OrderId, Guid AttemptId, string SessionId, Money Total, Guid UserId);

    public sealed record State(OrderStatus Status, string? RefundReason, PaymentAttemptStatus[] Attempts, int Tickets, string[] Outbox, RefundTracking Refund);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public FakeTimeProvider Time { get; }

    public GatewaySpy Spy { get; }

    /// <summary>Lets a test pause a transaction right after it was granted the event's sale lock.</summary>
    public SaleLockGate SaleLocks { get; } = new();

    public WebApplicationFactory<Program> Host { get; }

    public PaymentWorkerMetrics Metrics { get; }

    public Guid EventId { get; private set; }

    public (Guid Id, long Price) Seated { get; private set; }

    public (Guid Id, long Price) Ga { get; private set; }

    public IReadOnlyList<int> Seats { get; private set; } = [];

    public MockPaymentGateway Gateway => Host.Services.GetRequiredService<MockPaymentGateway>();

    public MockGatewayControl Control => Host.Services.GetRequiredService<MockGatewayControl>();

    public static async Task<CheckoutWorkersHarness> CreateAsync(ContainersFixture containers, IReadOnlyDictionary<string, string?>? settings = null)
    {
        var all = new Dictionary<string, string?>
        {
            // Own Redis namespace (leader locks, mock sessions) and no in-call retries: a fake clock never lets a backoff elapse.
            ["Redis:Environment"] = $"w{Guid.NewGuid():N}"[..12],
            ["Payments:Resilience:RetryCount"] = "0",
        };
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            all[key] = value;
        }

        var factory = new ApiFactory(await containers.CreateDatabaseAsync(), containers.RedisConnectionString, settings: all);
        // Whole microseconds, as Postgres stores them, so times read back compare equal to the clock's.
        var start = DateTimeOffset.UtcNow;
        var clock = new FakeTimeProvider(new DateTimeOffset(start.Ticks - (start.Ticks % 10), TimeSpan.Zero));
        var harness = new CheckoutWorkersHarness(factory, clock, new GatewaySpy());
        await harness.SeedAsync();
        return harness;
    }

    private async Task SeedAsync()
    {
        await using var scope = Host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DevDataSeeder>().SeedAsync(Ct);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var evt = await db.Events.AsNoTracking().Include(e => e.Tiers).SingleAsync(e => e.Slug == "son-tung-mtp-live", Ct);
        EventId = evt.Id;
        var seated = evt.Tiers.First(t => t.Kind == TicketKind.Seated);
        Seated = (seated.Id, seated.Price.Amount);
        var ga = evt.Tiers.First(t => t.Kind == TicketKind.GeneralAdmission);
        Ga = (ga.Id, ga.Price.Amount);
        Seats = await db.EventSeats.AsNoTracking().Where(s => s.EventId == EventId && s.TierId == seated.Id)
            .OrderBy(s => s.Index).Select(s => s.Index).Take(20).ToListAsync(Ct);
    }

    public OrderItem Seat(int seat) => new(Seated.Id, seat, new Money(Seated.Price), 1);

    public OrderItem GaUnits(int quantity) => new(Ga.Id, null, new Money(Ga.Price), quantity);

    public OrderExpiryService Sweeper(TimeProvider? clock = null) => new(
        Host.Services.GetRequiredService<IServiceScopeFactory>(), Metrics, Host.Services.GetRequiredService<IDistributedLock>(),
        clock ?? Time, NullLogger<OrderExpiryService>.Instance);

    /// <summary>The worker's backstop for cancelled events whose EventCancelled message never settled their orders.</summary>
    public CancelledEventSettlementService CancelledEvents(TimeProvider? clock = null) => new(
        Host.Services.GetRequiredService<IServiceScopeFactory>(), Metrics, Host.Services.GetRequiredService<EventCancellationMetrics>(),
        Host.Services.GetRequiredService<IDistributedLock>(), Host.Services.GetRequiredService<IOptions<CancelledEventSettlementOptions>>(),
        clock ?? Time, NullLogger<CancelledEventSettlementService>.Instance);

    /// <summary>Every SQL reader command of this host (inherited from the base factory).</summary>
    public QueryCounter Queries => _factory.Queries;

    public RefundWorker Refunds(TimeProvider? clock = null) => new(
        Host.Services.GetRequiredService<IServiceScopeFactory>(), Metrics, Host.Services.GetRequiredService<IDistributedLock>(),
        clock ?? Time, NullLogger<RefundWorker>.Instance);

    /// <summary>
    /// An order the buyer is paying for, with a real mock-gateway session (what <c>POST /orders/{id}/pay</c> leaves behind).
    /// <paramref name="outcome"/> completes the session on the gateway page; its webhook is queued but never delivered here.
    /// </summary>
    /// <param name="sessionOutlivesOrder">
    /// Lets the gateway session live this much longer than the order (normally it closes 30 s before), so the gateway still
    /// reports <c>Pending</c>, or still accepts a payment, after the order expired.
    /// </param>
    public async Task<Placed> PlaceAsync(OrderItem[] items, MockOutcome? outcome = null, TimeSpan? lifetime = null, TimeSpan? sessionOutlivesOrder = null)
    {
        var placed = await WithDbAsync(async db =>
        {
            var order = NewOrder(items, lifetime);
            order.StartPayment(Time.GetUtcNow());
            db.Orders.Add(order);
            return await AddSessionAsync(db, order, sessionOutlivesOrder);
        });
        if (outcome is { } done)
        {
            (await Control.CompleteAsync(placed.SessionId, done, Ct)).IsSuccess.ShouldBeTrue();
        }

        return placed;
    }

    /// <summary>A Pending order with no payment session (the buyer never pressed "Thanh toán").</summary>
    public Task<Guid> PlacePendingAsync(OrderItem[] items, TimeSpan? lifetime = null) => WithDbAsync(async db =>
    {
        var order = NewOrder(items, lifetime);
        db.Orders.Add(order);
        await db.SaveChangesAsync(Ct);
        return order.Id;
    });

    /// <summary>Another gateway session for an existing order (the buyer retried).</summary>
    public Task<Placed> AddSessionAsync(Guid orderId) => WithDbAsync(async db => await AddSessionAsync(db, await db.Orders.SingleAsync(o => o.Id == orderId, Ct)));

    private Order NewOrder(OrderItem[] items, TimeSpan? lifetime)
    {
        var now = Time.GetUtcNow();
        var total = items.Aggregate(Money.Zero(), (sum, item) => sum + item.LineTotal());
        return Order.Create(Guid.CreateVersion7(), EventId, Guid.CreateVersion7(), items, total, new BuyerInfo("Nguyễn Văn An", "an@example.com", null),
            now + (lifetime ?? TimeSpan.FromMinutes(8)), now);
    }

    private async Task<Placed> AddSessionAsync(AppDbContext db, Order order, TimeSpan? sessionOutlivesOrder = null)
    {
        var now = Time.GetUtcNow();
        var key = $"order:{order.Id:N}:attempt:{Guid.NewGuid():N}";
        var sessionExpiry = sessionOutlivesOrder is { } extra ? order.ExpiresAt + extra : order.ExpiresAt - PaymentAttempt.GatewayMargin;
        var session = await Gateway.CreateSessionAsync(order, new Uri($"http://localhost:3000/checkout/{order.Id}/result"), sessionExpiry, key, Ct);
        var attempt = PaymentAttempt.Create(order.Id, Provider, session.SessionId, session.RedirectUrl.AbsoluteUri, order.Total, order.ExpiresAt, now);
        db.PaymentAttempts.Add(attempt);
        await db.SaveChangesAsync(Ct);
        return new Placed(order.Id, attempt.Id, session.SessionId, order.Total, order.UserId);
    }

    /// <summary>A verified Succeeded webhook for the session, decided by the real fulfil path.</summary>
    public async Task<PaymentWebhookOutcome> DeliverSucceededAsync(Placed placed, Money? amount = null)
    {
        await using var scope = Host.Services.CreateAsyncScope();
        var webhook = new WebhookEvent($"evt_mock_{Guid.NewGuid():N}", placed.SessionId, WebhookEventType.Succeeded, amount ?? placed.Total, Time.GetUtcNow());
        return await scope.ServiceProvider.GetRequiredService<FulfillOrderHandler>().HandleAsync(Provider, webhook, Ct);
    }

    /// <summary>A verified Failed webhook for the session, decided by the real fulfil path.</summary>
    public async Task<PaymentWebhookOutcome> DeliverFailedAsync(Placed placed)
    {
        await using var scope = Host.Services.CreateAsyncScope();
        var webhook = new WebhookEvent($"evt_mock_{Guid.NewGuid():N}", placed.SessionId, WebhookEventType.Failed, null, Time.GetUtcNow());
        return await scope.ServiceProvider.GetRequiredService<FulfillOrderHandler>().HandleAsync(Provider, webhook, Ct);
    }

    /// <summary>A signed webhook posted to the api's real endpoint, signed at the fake clock's time.</summary>
    public async Task<string> PostWebhookAsync(Placed placed, long? amount, string? currency = null)
    {
        var body = new MockWebhookPayload($"evt_mock_{Guid.NewGuid():N}", MockWebhookPayload.SucceededType, placed.SessionId, amount, currency ?? "VND", Time.GetUtcNow()).ToJson();
        using var client = Host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/payments/mock") { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation(WebhookHeaders.SignatureHeader, WebhookSignature.Sign(TestSettingsSecret, body, Time.GetUtcNow()));
        using var response = await client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK, text);
        return System.Text.Json.JsonDocument.Parse(text).RootElement.GetProperty("outcome").GetString()!;
    }

    private const string TestSettingsSecret = "test-mock-webhook-secret-0123456789";

    public Task ChangeOrderAsync(Guid orderId, Action<Order> change) => WithDbAsync(async db =>
    {
        change(await db.Orders.SingleAsync(o => o.Id == orderId, Ct));
        return await db.SaveChangesAsync(Ct);
    });

    public Task ChangeAttemptAsync(Guid attemptId, Action<PaymentAttempt> change) => WithDbAsync(async db =>
    {
        change(await db.PaymentAttempts.SingleAsync(a => a.Id == attemptId, Ct));
        return await db.SaveChangesAsync(Ct);
    });

    public Task<State> StateAsync(Guid orderId) => WithDbAsync(async db =>
    {
        var order = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId, Ct);
        var attempts = await db.PaymentAttempts.AsNoTracking().Where(a => a.OrderId == orderId).OrderBy(a => a.CreatedAt).ThenBy(a => a.Id).ToListAsync(Ct);
        var ids = attempts.Select(a => (Guid?)a.Id).Append(orderId).ToList();
        var outbox = await db.OutboxMessages.AsNoTracking().Where(m => ids.Contains(m.AggregateId))
            .OrderBy(m => m.OccurredAt).Select(m => m.Type).ToArrayAsync(Ct);
        var tickets = await db.Tickets.CountAsync(t => t.OrderId == orderId, Ct);
        return new State(order.Status, order.RefundReason, [.. attempts.Select(a => a.Status)], tickets, outbox, order.Refund);
    });

    public Task<PaymentAttempt> AttemptAsync(Guid attemptId) =>
        WithDbAsync(db => db.PaymentAttempts.AsNoTracking().SingleAsync(a => a.Id == attemptId, Ct));

    public Task<int> OutboxCountAsync(string type) => WithDbAsync(db => db.OutboxMessages.CountAsync(m => m.Type == type, Ct));

    public Task<int> ProcessedWebhooksAsync(string providerEventId) =>
        WithDbAsync(db => db.ProcessedWebhooks.CountAsync(w => w.Provider == Provider && w.ProviderEventId == providerEventId, Ct));

    /// <summary>What the mock gateway has refunded on the session so far.</summary>
    public async Task<long> RefundedOnGatewayAsync(string sessionId) =>
        (await Host.Services.GetRequiredService<MockSessionStore>().GetAsync(sessionId, Ct)).ShouldNotBeNull().RefundedAmount;

    public async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        await using var scope = Host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public async ValueTask DisposeAsync()
    {
        await Host.DisposeAsync();
        await _factory.DisposeAsync();
    }

    /// <summary>Starts recording this host's worker metrics (other tests' hosts run in parallel and are ignored).</summary>
    public MetricRecorder RecordMetrics() => new(Host.Services.GetRequiredService<IMeterFactory>());

    /// <summary>Measurements of the instruments of one meter factory, as <c>name|tag=value,...</c> strings.</summary>
    internal sealed class MetricRecorder : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly System.Collections.Concurrent.ConcurrentQueue<(string Key, long Value)> _measurements = new();

        public MetricRecorder(IMeterFactory scope)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter.Scope, scope) && instrument.Meter.Name == PaymentWorkerMetrics.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
                _measurements.Enqueue(($"{instrument.Name}|{string.Join(",", tags.ToArray().Select(t => $"{t.Key}={t.Value}"))}", value)));
            _listener.Start();
        }

        /// <summary>Counter increments of <paramref name="name"/>, as their tag strings, in order.</summary>
        public IReadOnlyList<string> Counted(string name) =>
            [.. _measurements.Where(m => m.Key.StartsWith(name + "|", StringComparison.Ordinal)).Select(m => m.Key[(name.Length + 1)..])];

        /// <summary>The sum of the increments of <paramref name="name"/> with exactly these tags (<c>key=value,...</c>).</summary>
        public long Total(string name, string tags) => _measurements.Where(m => m.Key == $"{name}|{tags}").Sum(m => m.Value);

        /// <summary>The current value of an observable gauge.</summary>
        public long Gauge(string name)
        {
            _listener.RecordObservableInstruments();
            return _measurements.Last(m => m.Key.StartsWith(name + "|", StringComparison.Ordinal)).Value;
        }

        public void Dispose() => _listener.Dispose();
    }

    /// <summary>Counts the workers' gateway calls and runs optional hooks right after them.</summary>
    internal sealed class GatewaySpy
    {
        private int _statusCalls;
        private int _refundCalls;

        public int StatusCalls => Volatile.Read(ref _statusCalls);

        public int RefundCalls => Volatile.Read(ref _refundCalls);

        /// <summary>Runs after the gateway answered a status read, before the caller sees the answer.</summary>
        public Func<string, PaymentStatus, Task>? AfterStatus { get; set; }

        /// <summary>Runs before a refund call reaches the gateway, with its idempotency key (it may throw: an adapter failure).</summary>
        public Func<string, Task>? BeforeRefund { get; set; }

        /// <summary>Makes the fulfil path's dedupe insert throw for this provider event id (after earlier ones committed).</summary>
        public string? FailInboxFor { get; set; }

        /// <summary>Runs after the gateway answered a refund, before the caller sees the answer (it may throw: a crash).</summary>
        public Func<RefundResult, Task>? AfterRefund { get; set; }

        /// <summary>Idempotency keys of every refund call, in order.</summary>
        public List<string> RefundKeys { get; } = [];

        public void CountStatus() => Interlocked.Increment(ref _statusCalls);

        public void CountRefund(string key)
        {
            Interlocked.Increment(ref _refundCalls);
            lock (RefundKeys)
            {
                RefundKeys.Add(key);
            }
        }
    }

    /// <summary>Hooks into the event sale lock (<see cref="IEventSaleLock"/>).</summary>
    internal sealed class SaleLockGate
    {
        /// <summary>Runs right after a sale lock was granted (true = exclusive), inside the transaction that holds it.</summary>
        public Func<bool, Task>? AfterAcquire { get; set; }
    }

    /// <summary>The real sale lock, then the gate's hook.</summary>
    private sealed class GatedSaleLock(IEventSaleLock inner, SaleLockGate gate) : IEventSaleLock
    {
        public async Task<EventStatus?> LockStatusAsync(Guid eventId, CancellationToken ct)
        {
            var status = await inner.LockStatusAsync(eventId, ct);
            if (gate.AfterAcquire is { } hook)
            {
                await hook(false);
            }

            return status;
        }

        public async Task AcquireExclusiveAsync(Guid eventId, CancellationToken ct)
        {
            await inner.AcquireExclusiveAsync(eventId, ct);
            if (gate.AfterAcquire is { } hook)
            {
                await hook(true);
            }
        }
    }

    /// <summary>The real dedupe inbox, except that it throws once for <see cref="GatewaySpy.FailInboxFor"/>.</summary>
    private sealed class FaultyInbox(IWebhookInbox inner, GatewaySpy spy) : IWebhookInbox
    {
        public Task<bool> TryRecordAsync(string provider, string providerEventId, DateTimeOffset processedAt, CancellationToken ct)
        {
            if (spy.FailInboxFor is { } failing && failing == providerEventId)
            {
                spy.FailInboxFor = null;
                throw new InvalidOperationException("simulated failure of the second fulfilment");
            }

            return inner.TryRecordAsync(provider, providerEventId, processedAt, ct);
        }
    }

    private sealed class SpyResolver(IPaymentGatewayResolver inner, GatewaySpy spy) : IPaymentGatewayResolver
    {
        public IPaymentGateway Default => new SpyGateway(inner.Default, spy);

        public IPaymentGateway? Get(string provider) => inner.Get(provider) is { } gateway ? new SpyGateway(gateway, spy) : null;
    }

    private sealed class SpyGateway(IPaymentGateway inner, GatewaySpy spy) : IPaymentGateway
    {
        public string Provider => inner.Provider;

        public Task<PaymentSession> CreateSessionAsync(Order order, Uri returnUrl, DateTimeOffset expiresAt, string idempotencyKey, CancellationToken ct) =>
            inner.CreateSessionAsync(order, returnUrl, expiresAt, idempotencyKey, ct);

        public async Task<PaymentStatus> GetStatusAsync(string sessionId, CancellationToken ct)
        {
            spy.CountStatus();
            var status = await inner.GetStatusAsync(sessionId, ct);
            if (spy.AfterStatus is { } hook)
            {
                await hook(sessionId, status);
            }

            return status;
        }

        public async Task<RefundResult> RefundAsync(string sessionId, Money amount, string idempotencyKey, CancellationToken ct)
        {
            spy.CountRefund(idempotencyKey);
            if (spy.BeforeRefund is { } before)
            {
                await before(idempotencyKey);
            }

            var result = await inner.RefundAsync(sessionId, amount, idempotencyKey, ct);
            if (spy.AfterRefund is { } hook)
            {
                await hook(result);
            }

            return result;
        }

        public WebhookEvent VerifyAndParse(WebhookHeaders headers, string body) => inner.VerifyAndParse(headers, body);
    }
}
