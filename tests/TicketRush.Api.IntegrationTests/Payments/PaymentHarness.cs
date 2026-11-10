using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using StackExchange.Redis;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Application.Abstractions.Payments;
using Order = TicketRush.Domain.Orders.Order;
using TicketRush.Domain.Orders;
using TicketRush.Domain.Primitives;
using TicketRush.Infrastructure.Payments;
using TicketRush.Infrastructure.Redis;

namespace TicketRush.Api.IntegrationTests.Payments;

internal sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;

    public T Get(string? name) => value;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

/// <summary>Plays the api's webhook endpoint: records every delivery and answers with whatever the test scripted.</summary>
internal sealed class RecordingHandler : HttpMessageHandler
{
    public sealed record Received(Uri Url, string? Signature, string Body);

    private readonly object _gate = new();
    private readonly List<Received> _received = [];

    /// <summary>Status per call, in order; the last one repeats.</summary>
    public Queue<HttpStatusCode> Script { get; } = new();

    public Exception? Throw { get; set; }

    public IReadOnlyList<Received> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _received];
            }
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        var signature = request.Headers.TryGetValues(WebhookHeaders.SignatureHeader, out var values) ? values.Single() : null;
        lock (_gate)
        {
            _received.Add(new Received(request.RequestUri!, signature, body));
        }

        if (Throw is { } failure)
        {
            throw failure;
        }

        var status = Script.Count > 1 ? Script.Dequeue() : Script.Count == 1 ? Script.Peek() : HttpStatusCode.OK;
        return new HttpResponseMessage(status);
    }
}

/// <summary>An isolated Redis namespace, a fake clock and every mock-gateway collaborator wired by hand.</summary>
internal sealed class PaymentHarness : IAsyncDisposable
{
    public const string Secret = "unit-test-webhook-secret-0123456789";

    private PaymentHarness(ConnectionMultiplexer redis, RedisKeys keys, MockGatewayOptions options)
    {
        Redis = redis;
        Keys = keys;
        Options = options;
        var monitor = new StaticOptionsMonitor<MockGatewayOptions>(options);
        Queue = new MockWebhookQueue(redis, keys);
        Store = new MockSessionStore(redis, keys, Queue, monitor);
        Gateway = new MockPaymentGateway(Store, monitor, Time);
        Control = new MockGatewayControl(Store, monitor, Time);
        Handler = new RecordingHandler();
        Deliverer = new MockWebhookDeliverer(
            Queue, new HttpClient(Handler), monitor, Time, NullLogger<MockWebhookDeliverer>.Instance);
    }

    public ConnectionMultiplexer Redis { get; }

    public RedisKeys Keys { get; }

    public MockGatewayOptions Options { get; }

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.Zero));

    public MockWebhookQueue Queue { get; }

    public MockSessionStore Store { get; }

    public MockPaymentGateway Gateway { get; }

    public MockGatewayControl Control { get; }

    public RecordingHandler Handler { get; }

    public MockWebhookDeliverer Deliverer { get; }

    public static async Task<PaymentHarness> CreateAsync(ContainersFixture containers, Action<MockGatewayOptions>? configure = null)
    {
        var redis = await ConnectionMultiplexer.ConnectAsync(containers.RedisConnectionString);
        var keys = new RedisKeys(Microsoft.Extensions.Options.Options.Create(new RedisOptions { Environment = $"p{Guid.NewGuid():N}"[..12] }));
        var options = new MockGatewayOptions
        {
            Enabled = true,
            WebhookSecret = Secret,
            WebBaseUrl = "http://web.test:3000/",
            WebhookUrl = "http://api.test/api/v1/webhooks/payments/mock",
        };
        configure?.Invoke(options);
        return new PaymentHarness(redis, keys, options);
    }

    public static Order NewOrder(long unitPrice = 500_000, int quantity = 2) =>
        Order.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            [new OrderItem(Guid.NewGuid(), null, new Money(unitPrice), quantity)],
            new Money(unitPrice * quantity),
            new BuyerInfo("Nguyen Van A", "a@example.com", null),
            new DateTimeOffset(2026, 11, 4, 10, 8, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.Zero));

    public Task<PaymentSession> CreateSessionAsync(Order? order = null, string? key = null, TimeSpan? lifetime = null) =>
        Gateway.CreateSessionAsync(
            order ?? NewOrder(),
            new Uri("http://web.test:3000/checkout/result"),
            Time.GetUtcNow() + (lifetime ?? TimeSpan.FromMinutes(7)),
            key ?? Guid.NewGuid().ToString("N"),
            CancellationToken.None);

    /// <summary>Webhook headers as the receiving endpoint would see them.</summary>
    public static WebhookHeaders Headers(string? signature) =>
        new(signature is null ? [] : [KeyValuePair.Create(WebhookHeaders.SignatureHeader.ToLowerInvariant(), signature)]);

    public ValueTask DisposeAsync() => Redis.DisposeAsync();
}
