using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TicketRush.Api.IntegrationTests.Auth;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Infrastructure.Notifications;
using TicketRush.Infrastructure.Outbox;
using TicketRush.Infrastructure.Payments;
using TicketRush.Infrastructure.Persistence;
using TicketRush.Infrastructure.Persistence.Seed;
using TicketRush.Infrastructure.Queue;

namespace TicketRush.Api.IntegrationTests.Observability;

/// <summary>
/// Spec 17 acceptance: one purchase is one trace. The buyer's requests carry the checkout's traceparent; the mock gateway, the
/// outbox and the email queue store it and the next hop continues it, so hold → order → pay → webhook → outbox → email connect.
/// </summary>
public sealed class PurchaseTraceTests(ContainersFixture containers) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly ConcurrentQueue<Activity> _spans = new();
    private ActivityListener _listener = null!;
    private ApiFactory _factory = null!;
    private HttpClient _client = null!;
    private AuthApi _auth = null!;
    private Guid _event;
    private Guid _phase;

    public async ValueTask InitializeAsync()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name.StartsWith("TicketRush", StringComparison.Ordinal) || source.Name == "Microsoft.AspNetCore",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = _spans.Enqueue,
        };
        ActivitySource.AddActivityListener(_listener);

        _factory = new ApiFactory(await containers.CreateDatabaseAsync(), containers.RedisConnectionString, settings: new Dictionary<string, string?>
        {
            ["Redis:Environment"] = $"t{Guid.NewGuid():N}"[..12],
        });
        _client = AuthApi.CreateClient(_factory);
        _auth = new AuthApi(_factory, _client);
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DevDataSeeder>().SeedAsync(Ct);
        var evt = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Events.Include(e => e.Phases)
            .SingleAsync(e => e.Slug == "son-tung-mtp-live", Ct);
        (_event, _phase) = (evt.Id, evt.Phases.OrderBy(p => p.OpensAt).First().Id);
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Dispose();
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    /// <summary>What <c>HttpClientHandler</c>'s diagnostics do in production; the test server's handler has none.</summary>
    private sealed class PropagatingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            DistributedContextPropagator.Current.Inject(Activity.Current, request, static (carrier, key, value) =>
                ((HttpRequestMessage)carrier!).Headers.TryAddWithoutValidation(key, value));
            return base.SendAsync(request, cancellationToken);
        }
    }

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, string access, string traceParent, object? body = null, string? admission = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        request.Headers.Add("traceparent", traceParent);
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        if (admission is not null)
        {
            request.Headers.Add("X-Admission-Token", admission);
        }

        using var response = await _client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.IsSuccessStatusCode.ShouldBeTrue($"{method} {path}: {(int)response.StatusCode} {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    [Fact]
    public async Task A_purchase_is_one_trace_from_the_hold_to_the_confirmation_email()
    {
        var traceId = ActivityTraceId.CreateRandom();
        var traceParent = $"00-{traceId}-{ActivitySpanId.CreateRandom()}-01";
        var email = $"buyer-{Guid.NewGuid():N}@example.com";
        var access = (await _auth.SignInWithRoleAsync(email, "Customer")).AccessToken;

        // The waiting room (not part of the purchase trace: the checkout starts it at the hold).
        using (var join = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/events/{_event}/queue/join") { Content = JsonContent.Create(new { captchaToken = TicketRush.Infrastructure.Security.CaptchaOptions.FakePassToken }) })
        {
            join.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
            (await _client.SendAsync(join, Ct)).EnsureSuccessStatusCode();
        }

        string? admission = null;
        var admitter = _factory.Services.GetRequiredService<WaitingRoomAdmitter>();
        for (var i = 0; i < 200 && admission is null; i++)
        {
            await admitter.TickAsync(_phase, Ct);
            using var me = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/events/{_event}/queue/me");
            me.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
            var status = JsonDocument.Parse(await (await _client.SendAsync(me, Ct)).Content.ReadAsStringAsync(Ct)).RootElement;
            admission = status.GetProperty("status").GetString() == "Admitted" ? status.GetProperty("admissionToken").GetString() : null;
        }

        admission.ShouldNotBeNull();

        var hold = await SendAsync(HttpMethod.Post, $"/api/v1/events/{_event}/holds", access, traceParent, new { seatIndexes = new[] { 7 } }, admission);
        var order = await SendAsync(HttpMethod.Post, "/api/v1/orders", access, traceParent, new
        {
            eventId = _event,
            holdId = hold.GetProperty("holdId").GetGuid(),
            buyer = new { name = "Nguyễn Văn An", email, phone = "0901234567" },
        });
        var pay = await SendAsync(HttpMethod.Post, $"/api/v1/orders/{order.GetProperty("id").GetGuid()}/pay", access, traceParent);
        var session = new Uri(pay.GetProperty("redirectUrl").GetString()!).Segments[^1];

        // The buyer's choice on the gateway page carries no trace: the webhook continues the pay request's, stored with the session.
        (await _factory.Services.GetRequiredService<MockGatewayControl>().CompleteAsync(session, MockOutcome.Succeeded, Ct)).IsSuccess.ShouldBeTrue();
        using var webhookHttp = new HttpClient(new PropagatingHandler(_factory.Server.CreateHandler())) { BaseAddress = _client.BaseAddress };
        var deliverer = new MockWebhookDeliverer(
            _factory.Services.GetRequiredService<MockWebhookQueue>(), webhookHttp, _factory.Services.GetRequiredService<IOptionsMonitor<MockGatewayOptions>>(),
            TimeProvider.System, NullLogger<MockWebhookDeliverer>.Instance);
        (await deliverer.RunOnceAsync(Ct)).ShouldBe(1);

        await _factory.DrainOutboxAsync(Ct);
        while (await _factory.Services.GetRequiredService<EmailDeliveryProcessor>().ProcessBatchAsync(Ct) > 0)
        {
        }

        _factory.Emails.Sent.ShouldContain(m => m.To == email);

        var spans = _spans.Where(s => s.TraceId == traceId).ToList();
        var bySpan = spans.ToDictionary(s => s.SpanId);
        Activity Request(Func<string, bool> path) =>
            spans.Single(s => s.Kind == ActivityKind.Server && s.GetTagItem("http.request.method") as string == "POST"
                && path(s.GetTagItem("url.path") as string ?? string.Empty));

        Request(p => p.EndsWith("/holds", StringComparison.Ordinal)).ShouldNotBeNull();
        Request(p => p == "/api/v1/orders").ShouldNotBeNull();
        var payRequest = Request(p => p.EndsWith("/pay", StringComparison.Ordinal));
        var delivery = spans.Single(s => s.OperationName == "mock-gateway deliver");
        var webhook = Request(p => p.StartsWith("/api/v1/webhooks/", StringComparison.Ordinal));
        var outbox = spans.Single(s => s.OperationName == "outbox OrderPaid");
        var sent = spans.First(s => s.OperationName.StartsWith("email send ", StringComparison.Ordinal) && Ancestors(s, bySpan).Contains(outbox.SpanId));

        // The chain, parent by parent: pay → (session) → delivery → webhook request → outbox handling → email send.
        delivery.ParentSpanId.ShouldBe(payRequest.SpanId);
        webhook.ParentSpanId.ShouldBe(delivery.SpanId);
        Ancestors(outbox, bySpan).ShouldContain(webhook.SpanId);
        Ancestors(sent, bySpan).ShouldContain(outbox.SpanId);
    }

    private static List<ActivitySpanId> Ancestors(Activity span, Dictionary<ActivitySpanId, Activity> bySpan)
    {
        var chain = new List<ActivitySpanId>();
        for (var parent = span.ParentSpanId; parent != default; parent = bySpan.TryGetValue(parent, out var p) ? p.ParentSpanId : default)
        {
            chain.Add(parent);
        }

        return chain;
    }
}
