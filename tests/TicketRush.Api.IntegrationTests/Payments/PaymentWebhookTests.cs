using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Application.Abstractions.Inventory;
using TicketRush.Domain.Events;
using TicketRush.Domain.Orders;
using TicketRush.Domain.Primitives;
using TicketRush.Domain.Tickets;
using TicketRush.Infrastructure.Inventory;
using TicketRush.Infrastructure.Payments;
using TicketRush.Infrastructure.Persistence;
using TicketRush.Infrastructure.Persistence.Seed;
using Order = TicketRush.Domain.Orders.Order;

namespace TicketRush.Api.IntegrationTests.Payments;

/// <summary>
/// Spec 08 "Webhook handling" end to end over HTTP against Postgres: signature gate (401, nothing changes), exactly one
/// decision per provider event (C4), late payments (C3), the database as final truth for seats and capacity (C1).
/// Orders and attempts are written straight to the database in the state each case needs; webhooks are signed with the
/// mock gateway's real <see cref="WebhookSignature"/> and the test secret.
/// </summary>
public sealed class PaymentWebhookTests(ContainersFixture containers) : IAsyncLifetime
{
    private const string Secret = "test-mock-webhook-secret-0123456789";
    private const string Path = "/api/v1/webhooks/payments/mock";
    private const int MaxBody = 64 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private ApiFactory _factory = null!;
    private HttpClient _client = null!;
    private Guid _event;
    private (Guid Id, long Price, int Capacity) _ga;
    private (Guid Id, long Price) _seated;
    private List<int> _seats = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _factory = new ApiFactory(await containers.CreateDatabaseAsync(), containers.RedisConnectionString);
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DevDataSeeder>().SeedAsync(Ct);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var evt = await db.Events.AsNoTracking().Include(e => e.Tiers).SingleAsync(e => e.Slug == "son-tung-mtp-live", Ct);
        _event = evt.Id;
        var ga = evt.Tiers.First(t => t.Kind == TicketKind.GeneralAdmission);
        _ga = (ga.Id, ga.Price.Amount, ga.Capacity!.Value);
        var seated = evt.Tiers.First(t => t.Kind == TicketKind.Seated);
        _seated = (seated.Id, seated.Price.Amount);
        _seats = await db.EventSeats.AsNoTracking().Where(s => s.EventId == _event && s.TierId == seated.Id)
            .OrderBy(s => s.Index).Select(s => s.Index).Take(40).ToListAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    // ---------------------------------------------------------------- fixtures

    private sealed record Placed(Guid OrderId, Guid AttemptId, string SessionId, long Total, Guid UserId);

    private OrderItem Seat(int seat) => new(_seated.Id, seat, new Money(_seated.Price), 1);

    private OrderItem Ga(int quantity) => new(_ga.Id, null, new Money(_ga.Price), quantity);

    /// <summary>An order that is waiting for its gateway session (what <c>POST /orders/{id}/pay</c> leaves behind).</summary>
    private Task<Placed> PlaceAsync(params OrderItem[] items) => PlaceForAsync(Guid.CreateVersion7(), items);

    private Task<Placed> PlaceForAsync(Guid userId, params OrderItem[] items) => WithDbAsync(async db =>
    {
        var now = DateTimeOffset.UtcNow;
        var total = items.Aggregate(Money.Zero(), (sum, item) => sum + item.LineTotal());
        var order = Order.Create(userId, _event, Guid.CreateVersion7(), items, total, new BuyerInfo("Nguyễn Văn An", "an@example.com", null), now.AddMinutes(8), now);
        order.StartPayment(now);
        var placed = await AddAttemptAsync(db, order, now);
        return placed;
    });

    private static async Task<Placed> AddAttemptAsync(AppDbContext db, Order order, DateTimeOffset now)
    {
        var session = $"sess_{Guid.NewGuid():N}";
        var attempt = PaymentAttempt.Create(order.Id, "mock", session, $"http://localhost:3000/mock-gateway/{session}", order.Total, order.ExpiresAt, now);
        if (db.Entry(order).State == EntityState.Detached)
        {
            db.Orders.Add(order);
        }

        db.PaymentAttempts.Add(attempt);
        await db.SaveChangesAsync(Ct);
        return new Placed(order.Id, attempt.Id, session, order.Total.Amount, order.UserId);
    }

    /// <summary>A second gateway session for the same order (e.g. the buyer retried after the first page seemed stuck).</summary>
    private Task<Placed> SecondAttemptAsync(Placed placed) => WithDbAsync(async db =>
        await AddAttemptAsync(db, await db.Orders.SingleAsync(o => o.Id == placed.OrderId, Ct), DateTimeOffset.UtcNow));

    private Task<int> ChangeOrderAsync(Guid orderId, Action<Order> change) => WithDbAsync(async db =>
    {
        change(await db.Orders.SingleAsync(o => o.Id == orderId, Ct));
        await db.SaveChangesAsync(Ct);
        return 0;
    });

    private async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    // ---------------------------------------------------------------- sending

    private static string Body(string session, string type = MockWebhookPayload.SucceededType, long? amount = null, string? eventId = null, string currency = "VND") =>
        new MockWebhookPayload(eventId ?? $"evt_mock_{Guid.NewGuid():N}", type, session, amount, currency, DateTimeOffset.UtcNow).ToJson();

    private static string Sign(string body, DateTimeOffset? at = null) => WebhookSignature.Sign(Secret, body, at ?? DateTimeOffset.UtcNow);

    private Task<HttpResponseMessage> PostAsync(byte[] body, string? signature, string path = Path, HttpClient? client = null, bool unknownLength = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = unknownLength ? new UnknownLengthContent(body) : new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (signature is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Signature", signature);
        }

        return (client ?? _client).SendAsync(request, Ct);
    }

    private Task<HttpResponseMessage> DeliverAsync(string body, HttpClient? client = null) => PostAsync(Encoding.UTF8.GetBytes(body), Sign(body), client: client);

    private Task<HttpResponseMessage> SucceedAsync(Placed placed, string? eventId = null, HttpClient? client = null) =>
        DeliverAsync(Body(placed.SessionId, amount: placed.Total, eventId: eventId), client);

    private static async Task<string> OutcomeAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, text);
        return JsonDocument.Parse(text).RootElement.GetProperty("outcome").GetString()!;
    }

    private static async Task ShouldBeProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(status, text);
        JsonDocument.Parse(text).RootElement.GetProperty("code").GetString().ShouldBe(code);
    }

    // ---------------------------------------------------------------- reading the database

    private sealed record OrderState(OrderStatus Status, string? RefundReason, PaymentAttemptStatus[] Attempts, int Tickets, string[] Outbox);

    private Task<OrderState> StateAsync(Guid orderId) => WithDbAsync(async db =>
    {
        var order = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId, Ct);
        var attempts = await db.PaymentAttempts.AsNoTracking().Where(a => a.OrderId == orderId).OrderBy(a => a.CreatedAt).Select(a => a.Status).ToArrayAsync(Ct);
        var attemptIds = await db.PaymentAttempts.AsNoTracking().Where(a => a.OrderId == orderId).Select(a => (Guid?)a.Id).ToListAsync(Ct);
        var tickets = await db.Tickets.CountAsync(t => t.OrderId == orderId, Ct);
        var outbox = await db.OutboxMessages.AsNoTracking().Where(m => m.AggregateId == orderId || attemptIds.Contains(m.AggregateId))
            .OrderBy(m => m.OccurredAt).Select(m => m.Type).ToArrayAsync(Ct);
        return new OrderState(order.Status, order.RefundReason, attempts, tickets, outbox);
    });

    private sealed record Totals(int Webhooks, int Tickets, int Outbox, int Orders, int SoldCount, string Statuses);

    /// <summary>Everything a webhook could write, for "nothing changed" checks.</summary>
    private Task<Totals> TotalsAsync() => WithDbAsync(async db => new Totals(
        await db.ProcessedWebhooks.CountAsync(Ct),
        await db.Tickets.CountAsync(Ct),
        await db.OutboxMessages.CountAsync(Ct),
        await db.Orders.CountAsync(Ct),
        await SoldCountAsync(db),
        string.Join(",", await db.Orders.AsNoTracking().OrderBy(o => o.Id).Select(o => o.Status.ToString() + "/" + o.UpdatedAt).ToListAsync(Ct))
            + "|" + string.Join(",", await db.PaymentAttempts.AsNoTracking().OrderBy(a => a.Id).Select(a => a.Status.ToString() + "/" + a.UpdatedAt).ToListAsync(Ct))));

    private Task<int> SoldCountAsync(AppDbContext db) =>
        db.Database.SqlQuery<int>($"SELECT sold_count AS \"Value\" FROM ticket_tiers WHERE id = {_ga.Id}").SingleAsync(Ct);

    private Task<int> SoldCountAsync() => WithDbAsync(SoldCountAsync);

    private Task<int> WebhookRowsAsync(string providerEventId) =>
        WithDbAsync(db => db.ProcessedWebhooks.CountAsync(w => w.Provider == "mock" && w.ProviderEventId == providerEventId, Ct));

    /// <summary>
    /// The spec's SQL invariant (C1), from <c>load-tests/check-invariants.sql</c>: per seat at most one non-voided ticket, per GA tier
    /// <c>sold_count &lt;= capacity</c> and <c>sold_count</c> equal to the tier's non-voided GA tickets.
    /// </summary>
    private Task<int> AssertInventoryInvariantsAsync() => WithDbAsync(db => SaleInvariants.AssertInventoryAsync(db, Ct));

    // ================================================================ signature gate

    public static TheoryData<string> BadSignatures() => ["forged", "tampered_body", "six_minutes_old", "six_minutes_ahead", "missing", "malformed"];

    [Theory]
    [MemberData(nameof(BadSignatures))]
    public async Task A_webhook_that_fails_the_signature_check_is_401_and_changes_nothing(string kind)
    {
        var placed = await PlaceAsync(Seat(_seats[0]), Ga(1));
        var body = Body(placed.SessionId, amount: placed.Total);
        var before = await TotalsAsync();
        var now = DateTimeOffset.UtcNow;
        var (sent, signature) = kind switch
        {
            "forged" => (body, WebhookSignature.Sign("another-secret-of-the-same-length-01", body, now)),
            "tampered_body" => (body.Replace(placed.Total.ToString(System.Globalization.CultureInfo.InvariantCulture), "1", StringComparison.Ordinal), Sign(body)),
            "six_minutes_old" => (body, Sign(body, now.AddMinutes(-6))),
            "six_minutes_ahead" => (body, Sign(body, now.AddMinutes(6))),
            "missing" => (body, (string?)null),
            _ => (body, "t=abc,v1=zz"),
        };

        var response = await PostAsync(Encoding.UTF8.GetBytes(sent), signature);

        await ShouldBeProblemAsync(response, HttpStatusCode.Unauthorized, "webhook.invalid_signature");
        (await TotalsAsync()).ShouldBe(before);
        (await StateAsync(placed.OrderId)).Status.ShouldBe(OrderStatus.AwaitingPayment);
    }

    [Theory]
    [InlineData("bom")]
    [InlineData("invalid_utf8")]
    public async Task A_body_that_is_not_plain_utf8_fails_closed(string kind)
    {
        var placed = await PlaceAsync(Ga(1));
        var body = Body(placed.SessionId, amount: placed.Total);
        var signature = Sign(body);
        var bytes = kind == "bom"
            ? [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(body)]
            : Encoding.UTF8.GetBytes(body).Append((byte)0xC3).ToArray();
        var before = await TotalsAsync();

        var response = await PostAsync(bytes, signature);

        await ShouldBeProblemAsync(response, HttpStatusCode.Unauthorized, "webhook.invalid_signature");
        (await TotalsAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task A_body_over_64_KB_is_413_and_one_of_exactly_64_KB_is_processed()
    {
        var placed = await PlaceAsync(Ga(1));
        var json = Body(placed.SessionId, amount: placed.Total);
        var exact = json + new string(' ', MaxBody - Encoding.UTF8.GetByteCount(json));
        var over = exact + " ";
        var before = await TotalsAsync();

        var tooBig = await PostAsync(Encoding.UTF8.GetBytes(over), Sign(over));
        var unknownLength = await PostAsync(Encoding.UTF8.GetBytes(over), Sign(over), unknownLength: true);

        await ShouldBeProblemAsync(tooBig, HttpStatusCode.RequestEntityTooLarge, "webhook.too_large");
        await ShouldBeProblemAsync(unknownLength, HttpStatusCode.RequestEntityTooLarge, "webhook.too_large");
        (await TotalsAsync()).ShouldBe(before);
        (await OutcomeAsync(await DeliverAsync(exact))).ShouldBe("paid");
    }

    [Fact]
    public async Task An_unknown_provider_is_404()
    {
        var placed = await PlaceAsync(Ga(1));
        var body = Body(placed.SessionId, amount: placed.Total);

        var response = await PostAsync(Encoding.UTF8.GetBytes(body), Sign(body), "/api/v1/webhooks/payments/vnpay");

        await ShouldBeProblemAsync(response, HttpStatusCode.NotFound, "webhook.unknown_provider");
        (await StateAsync(placed.OrderId)).Status.ShouldBe(OrderStatus.AwaitingPayment);
    }

    [Fact]
    public async Task The_provider_segment_is_case_insensitive()
    {
        var placed = await PlaceAsync(Ga(1));
        var body = Body(placed.SessionId, amount: placed.Total);

        var response = await PostAsync(Encoding.UTF8.GetBytes(body), Sign(body), "/api/v1/webhooks/payments/MOCK");

        (await OutcomeAsync(response)).ShouldBe("paid");
    }

    [Fact]
    public async Task An_authentic_body_that_is_not_a_known_event_is_400_and_changes_nothing()
    {
        var placed = await PlaceAsync(Ga(1));
        var body = Body(placed.SessionId, type: "payment.exploded", amount: placed.Total);
        var before = await TotalsAsync();

        var response = await DeliverAsync(body);

        await ShouldBeProblemAsync(response, HttpStatusCode.BadRequest, "webhook.invalid_payload");
        (await TotalsAsync()).ShouldBe(before);
    }

    [Fact]
    public async Task A_blank_webhook_secret_fails_closed_with_500()
    {
        var settings = new Dictionary<string, string?> { ["Payments:Mock:Enabled"] = "false", ["Payments:Mock:WebhookSecret"] = "" };
        await using var host = new ApiFactory(await containers.CreateDatabaseAsync(), containers.RedisConnectionString, "Development", settings);
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var body = Body("sess_whatever");

        var forgedWithEmptyKey = await PostAsync(Encoding.UTF8.GetBytes(body), Sign(body), client: client);

        await ShouldBeProblemAsync(forgedWithEmptyKey, HttpStatusCode.InternalServerError, "webhook.misconfigured");
    }

    [Fact]
    public async Task Every_webhook_is_counted_by_outcome()
    {
        var hostMeters = _factory.Services.GetRequiredService<IMeterFactory>();
        var outcomes = new ConcurrentQueue<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "ticketrush.payments.webhooks" && ReferenceEquals(instrument.Meter.Scope, hostMeters))
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "outcome")
                {
                    outcomes.Enqueue((string)tag.Value!);
                }
            }
        });
        listener.Start();
        var placed = await PlaceAsync(Ga(1));
        var body = Body(placed.SessionId, amount: placed.Total);

        await DeliverAsync(body);
        await DeliverAsync(body);
        await PostAsync(Encoding.UTF8.GetBytes(body), WebhookSignature.Sign("another-secret-of-the-same-length-01", body, DateTimeOffset.UtcNow));

        outcomes.ShouldBe(["paid", "duplicate", "invalid_signature"]);
    }

    // ================================================================ exactly once (C4)

    [Fact]
    public async Task The_same_webhook_five_times_at_once_issues_tickets_once()
    {
        var placed = await PlaceAsync(Seat(_seats[0]), Seat(_seats[1]), Ga(2));
        var body = Body(placed.SessionId, amount: placed.Total);
        var soldBefore = await SoldCountAsync();
        // Barrier: the delivery that recorded the event holds its transaction open until the four others are all
        // blocked on its uncommitted processed_webhooks row, so the five really overlap.
        var (host, client) = HookedHost(before: async (_, call) =>
        {
            if (call == 1)
            {
                await WaitForLockWaitersAsync("transactionid", 4);
            }
        });
        await using var hostLease = host;
        using var clientLease = client;

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => DeliverAsync(body, client)));

        var outcomes = await Task.WhenAll(responses.Select(OutcomeAsync));
        outcomes.Order().ShouldBe(["duplicate", "duplicate", "duplicate", "duplicate", "paid"]);
        var state = await StateAsync(placed.OrderId);
        state.Status.ShouldBe(OrderStatus.Paid);
        state.Tickets.ShouldBe(4);
        state.Attempts.ShouldBe([PaymentAttemptStatus.Succeeded]);
        state.Outbox.Count(t => t == nameof(OrderPaid)).ShouldBe(1);
        (await SoldCountAsync()).ShouldBe(soldBefore + 2);
        (await WithDbAsync(db => db.ProcessedWebhooks.CountAsync(Ct))).ShouldBe(1);
        await AssertInventoryInvariantsAsync();
    }

    [Fact]
    public async Task The_paid_order_gets_one_order_paid_outbox_row_with_its_lines()
    {
        var placed = await PlaceAsync(Seat(_seats[3]), Ga(2));

        (await OutcomeAsync(await SucceedAsync(placed))).ShouldBe("paid");

        var payload = await WithDbAsync(db => db.OutboxMessages.AsNoTracking()
            .Where(m => m.AggregateId == placed.OrderId && m.Type == nameof(OrderPaid)).Select(m => m.Payload).SingleAsync(Ct));
        var paid = JsonSerializer.Deserialize<OrderPaid>(payload, Json)!;
        paid.OrderId.ShouldBe(placed.OrderId);
        paid.TicketCount.ShouldBe(3);
        paid.Lines.ShouldBe([new OrderPaidLine(_seated.Id, _seats[3], 1), new OrderPaidLine(_ga.Id, null, 2)]);
        var tickets = await WithDbAsync(db => db.Tickets.AsNoTracking().Where(t => t.OrderId == placed.OrderId).ToListAsync(Ct));
        tickets.ShouldAllBe(t => t.Status == TicketStatus.Valid && t.OwnerUserId == placed.UserId && t.EventId == _event);
        tickets.Select(t => t.QrSecret).Distinct(new BytesComparer()).Count().ShouldBe(3);
    }

    [Fact]
    public async Task A_redelivery_after_the_decision_is_200_duplicate_and_changes_nothing()
    {
        var placed = await PlaceAsync(Ga(1));
        var body = Body(placed.SessionId, amount: placed.Total);
        (await OutcomeAsync(await DeliverAsync(body))).ShouldBe("paid");
        var before = await TotalsAsync();

        (await OutcomeAsync(await DeliverAsync(body))).ShouldBe("duplicate");

        (await TotalsAsync()).ShouldBe(before);
    }

    // ================================================================ out of order

    [Fact]
    public async Task A_failed_webhook_after_success_leaves_the_order_paid()
    {
        var placed = await PlaceAsync(Ga(2));
        (await OutcomeAsync(await SucceedAsync(placed))).ShouldBe("paid");

        var failed = await DeliverAsync(Body(placed.SessionId, MockWebhookPayload.FailedType, placed.Total));

        (await OutcomeAsync(failed)).ShouldBe("ignored");
        var state = await StateAsync(placed.OrderId);
        state.Status.ShouldBe(OrderStatus.Paid);
        state.Attempts.ShouldBe([PaymentAttemptStatus.Succeeded]);
        state.Tickets.ShouldBe(2);
        (await WithDbAsync(db => db.ProcessedWebhooks.CountAsync(Ct))).ShouldBe(2);
    }

    [Fact]
    public async Task A_failed_webhook_on_an_awaiting_order_fails_the_attempt_and_the_order()
    {
        var placed = await PlaceAsync(Ga(1));

        var failed = await DeliverAsync(Body(placed.SessionId, MockWebhookPayload.FailedType, placed.Total));

        (await OutcomeAsync(failed)).ShouldBe("failed");
        var state = await StateAsync(placed.OrderId);
        state.Status.ShouldBe(OrderStatus.PaymentFailed);
        state.Attempts.ShouldBe([PaymentAttemptStatus.Failed]);
        state.Tickets.ShouldBe(0);
    }

    [Fact]
    public async Task A_late_failure_of_an_old_session_does_not_fail_an_order_another_live_session_can_still_pay()
    {
        var placed = await PlaceAsync(Ga(1));
        await SecondAttemptAsync(placed);

        var failed = await DeliverAsync(Body(placed.SessionId, MockWebhookPayload.FailedType, placed.Total));

        (await OutcomeAsync(failed)).ShouldBe("failed");
        var state = await StateAsync(placed.OrderId);
        state.Status.ShouldBe(OrderStatus.AwaitingPayment);
        state.Attempts.ShouldBe([PaymentAttemptStatus.Failed, PaymentAttemptStatus.Pending]);
    }

    [Fact]
    public async Task A_success_after_a_failure_still_pays_the_order()
    {
        var placed = await PlaceAsync(Ga(1));
        (await OutcomeAsync(await DeliverAsync(Body(placed.SessionId, MockWebhookPayload.FailedType, placed.Total)))).ShouldBe("failed");

        (await OutcomeAsync(await SucceedAsync(placed))).ShouldBe("paid");

        var state = await StateAsync(placed.OrderId);
        state.Status.ShouldBe(OrderStatus.Paid);
        state.Attempts.ShouldBe([PaymentAttemptStatus.Succeeded]);
        state.Tickets.ShouldBe(1);
    }

    [Fact]
    public async Task A_refund_confirmation_is_recorded_and_ignored()
    {
        var placed = await PlaceAsync(Ga(1));

        var refunded = await DeliverAsync(Body(placed.SessionId, MockWebhookPayload.RefundedType, placed.Total));

        (await OutcomeAsync(refunded)).ShouldBe("ignored");
        (await StateAsync(placed.OrderId)).Status.ShouldBe(OrderStatus.AwaitingPayment);
        (await WithDbAsync(db => db.ProcessedWebhooks.CountAsync(Ct))).ShouldBe(1);
    }

    [Fact]
    public async Task An_unknown_session_is_200_and_nothing_is_written()
    {
        await PlaceAsync(Ga(1));
        var before = await TotalsAsync();

        var response = await DeliverAsync(Body($"sess_{Guid.NewGuid():N}", amount: 100));

        (await OutcomeAsync(response)).ShouldBe("unknown_session");
        (await TotalsAsync()).ShouldBe(before);
    }

    // ================================================================ late payment (C3)

    [Fact]
    public async Task A_late_success_after_expiry_with_the_seats_still_free_pays_the_order()
    {
        var placed = await PlaceAsync(Seat(_seats[0]), Ga(1));
        await ChangeOrderAsync(placed.OrderId, o => o.Expire());
        var soldBefore = await SoldCountAsync();

        (await OutcomeAsync(await SucceedAsync(placed))).ShouldBe("paid");

        var state = await StateAsync(placed.OrderId);
        state.Status.ShouldBe(OrderStatus.Paid);
        state.Tickets.ShouldBe(2);
        state.Outbox.ShouldBe([nameof(OrderExpired), nameof(OrderPaid)]);
        (await SoldCountAsync()).ShouldBe(soldBefore + 1);
        await AssertInventoryInvariantsAsync();
    }

    [Fact]
    public async Task A_late_success_after_the_seat_was_resold_goes_to_refund_and_leaves_no_claim_behind()
    {
        var late = await PlaceAsync(Seat(_seats[0]), Ga(2));
        await ChangeOrderAsync(late.OrderId, o => o.Expire());
        var resold = await PlaceAsync(Seat(_seats[0]));
        (await OutcomeAsync(await SucceedAsync(resold))).ShouldBe("paid");
        var soldBefore = await SoldCountAsync();
        var eventId = $"evt_mock_{Guid.NewGuid():N}";

        (await OutcomeAsync(await SucceedAsync(late, eventId))).ShouldBe("refund_pending");

        var state = await StateAsync(late.OrderId);
        state.Status.ShouldBe(OrderStatus.RefundPending);
        state.RefundReason.ShouldBe(RefundReasons.SeatsUnavailable);
        state.Tickets.ShouldBe(0);
        state.Attempts.ShouldBe([PaymentAttemptStatus.Succeeded]);
        state.Outbox.ShouldBe([nameof(OrderExpired), nameof(RefundRequested)]);
        (await SoldCountAsync()).ShouldBe(soldBefore, "the GA claim of the rolled back transaction must not survive");
        (await WebhookRowsAsync(eventId)).ShouldBe(1);
        (await StateAsync(resold.OrderId)).Tickets.ShouldBe(1);
        await AssertInventoryInvariantsAsync();
    }

    [Fact]
    public async Task A_sold_out_general_admission_tier_sends_the_late_payment_to_refund_with_sold_count_unchanged()
    {
        var placed = await PlaceAsync(Ga(2));
        await WithDbAsync(db => db.Database.ExecuteSqlAsync($"UPDATE ticket_tiers SET capacity = sold_count + 1 WHERE id = {_ga.Id}", Ct));
        var soldBefore = await SoldCountAsync();

        (await OutcomeAsync(await SucceedAsync(placed))).ShouldBe("refund_pending");

        var state = await StateAsync(placed.OrderId);
        state.Status.ShouldBe(OrderStatus.RefundPending);
        state.Tickets.ShouldBe(0);
        state.Outbox.ShouldBe([nameof(RefundRequested)]);
        (await SoldCountAsync()).ShouldBe(soldBefore);
    }

    [Fact]
    public async Task A_buyer_over_the_per_user_limit_is_refunded_instead_of_getting_more_tickets()
    {
        var buyer = Guid.CreateVersion7();
        var first = await PlaceForAsync(buyer, Ga(3));
        var second = await PlaceForAsync(buyer, Ga(2));
        (await OutcomeAsync(await SucceedAsync(first))).ShouldBe("paid");
        var soldBefore = await SoldCountAsync();

        (await OutcomeAsync(await SucceedAsync(second))).ShouldBe("refund_pending");

        (await StateAsync(second.OrderId)).Status.ShouldBe(OrderStatus.RefundPending);
        (await StateAsync(second.OrderId)).RefundReason.ShouldBe(RefundReasons.PurchaseLimitExceeded);
        (await StateAsync(second.OrderId)).Tickets.ShouldBe(0);
        (await SoldCountAsync()).ShouldBe(soldBefore);
        (await WithDbAsync(db => db.Tickets.CountAsync(t => t.OwnerUserId == buyer, Ct))).ShouldBe(3);
    }

    [Fact]
    public async Task Exactly_the_limit_is_still_allowed()
    {
        var buyer = Guid.CreateVersion7();
        var first = await PlaceForAsync(buyer, Ga(3));
        var second = await PlaceForAsync(buyer, Seat(_seats[5]));
        (await OutcomeAsync(await SucceedAsync(first))).ShouldBe("paid");

        (await OutcomeAsync(await SucceedAsync(second))).ShouldBe("paid");

        (await WithDbAsync(db => db.Tickets.CountAsync(t => t.OwnerUserId == buyer, Ct))).ShouldBe(4);
    }

    [Theory]
    [InlineData("seat_taken")]
    [InlineData("ga_sold_out")]
    public async Task A_mixed_order_whose_one_part_conflicts_leaves_nothing_of_the_other_part(string conflict)
    {
        var placed = await PlaceAsync(Seat(_seats[1]), Seat(_seats[2]), Ga(2));
        if (conflict == "seat_taken")
        {
            var other = await PlaceAsync(Seat(_seats[2]));
            (await OutcomeAsync(await SucceedAsync(other))).ShouldBe("paid");
        }
        else
        {
            await WithDbAsync(db => db.Database.ExecuteSqlAsync($"UPDATE ticket_tiers SET capacity = sold_count + 1 WHERE id = {_ga.Id}", Ct));
        }

        var soldBefore = await SoldCountAsync();

        (await OutcomeAsync(await SucceedAsync(placed))).ShouldBe("refund_pending");

        (await StateAsync(placed.OrderId)).Tickets.ShouldBe(0);
        (await WithDbAsync(db => db.Tickets.CountAsync(t => t.SeatIndex == _seats[1], Ct))).ShouldBe(0, "the free seat of the order must not keep a ticket");
        (await SoldCountAsync()).ShouldBe(soldBefore);
        await AssertInventoryInvariantsAsync();
    }

    [Theory]
    [InlineData("Cancelled", RefundReasons.EventCancelled)]
    [InlineData("Ended", RefundReasons.EventEnded)]
    public async Task A_success_for_an_event_no_longer_on_sale_issues_no_tickets_and_goes_to_refund(string eventStatus, string reason)
    {
        var placed = await PlaceAsync(Seat(_seats[9]), Ga(1));
        await WithDbAsync(db => db.Database.ExecuteSqlAsync($"UPDATE events SET status = {eventStatus} WHERE id = {_event}", Ct));
        var soldBefore = await SoldCountAsync();

        (await OutcomeAsync(await SucceedAsync(placed))).ShouldBe("refund_pending");

        var state = await StateAsync(placed.OrderId);
        state.Status.ShouldBe(OrderStatus.RefundPending);
        state.RefundReason.ShouldBe(reason);
        state.Tickets.ShouldBe(0);
        state.Attempts.ShouldBe([PaymentAttemptStatus.Succeeded]);
        state.Outbox.ShouldBe([nameof(RefundRequested)]);
        (await SoldCountAsync()).ShouldBe(soldBefore);
    }

    // ================================================================ money on the wrong session

    [Fact]
    public async Task A_second_session_paying_an_already_paid_order_is_refunded_once_and_issues_no_tickets()
    {
        var placed = await PlaceAsync(Seat(_seats[4]), Ga(1));
        var second = await SecondAttemptAsync(placed);
        (await OutcomeAsync(await SucceedAsync(placed))).ShouldBe("paid");

        (await OutcomeAsync(await SucceedAsync(second))).ShouldBe("refund_pending");
        (await OutcomeAsync(await SucceedAsync(second))).ShouldBe("ignored");

        var state = await StateAsync(placed.OrderId);
        state.Status.ShouldBe(OrderStatus.Paid);
        state.Tickets.ShouldBe(2);
        state.Attempts.ShouldBe([PaymentAttemptStatus.Succeeded, PaymentAttemptStatus.RefundPending]);
        state.Outbox.Count(t => t == nameof(PaymentRefundRequested)).ShouldBe(1);
        state.Outbox.Count(t => t == nameof(OrderPaid)).ShouldBe(1);
        var refund = await WithDbAsync(db => db.OutboxMessages.AsNoTracking().SingleAsync(m => m.Type == nameof(PaymentRefundRequested), Ct));
        var payload = JsonSerializer.Deserialize<PaymentRefundRequested>(refund.Payload, Json)!;
        payload.ShouldBe(new PaymentRefundRequested(second.AttemptId, placed.OrderId, "mock", second.SessionId, placed.Total, "VND", RefundReasons.DuplicatePayment));
        await AssertInventoryInvariantsAsync();
    }

    [Fact]
    public async Task Two_sessions_of_one_order_succeeding_at_once_pay_it_once_and_refund_the_other()
    {
        var placed = await PlaceAsync(Seat(_seats[6]), Ga(1));
        var second = await SecondAttemptAsync(placed);
        // Barrier: whichever session wins the buyer lock first waits until the other is blocked on it.
        var (host, client) = HookedHost(after: async (_, call) =>
        {
            if (call == 1)
            {
                await WaitForLockWaitersAsync("advisory", 1);
            }
        });
        await using var hostLease = host;
        using var clientLease = client;

        var outcomes = await Task.WhenAll(new[] { placed, second }.Select(async p => await OutcomeAsync(await SucceedAsync(p, client: client))));

        outcomes.Order().ShouldBe(["paid", "refund_pending"]);
        var state = await StateAsync(placed.OrderId);
        state.Status.ShouldBe(OrderStatus.Paid);
        state.Tickets.ShouldBe(2);
        state.Attempts.Order().ShouldBe([PaymentAttemptStatus.Succeeded, PaymentAttemptStatus.RefundPending]);
        state.Outbox.Count(t => t == nameof(PaymentRefundRequested)).ShouldBe(1);
        await AssertInventoryInvariantsAsync();
    }

    [Fact]
    public async Task Money_for_a_cancelled_order_is_refunded_and_the_order_stays_cancelled()
    {
        var placed = await WithDbAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            var order = Order.Create(Guid.CreateVersion7(), _event, Guid.CreateVersion7(), [Ga(1)], new Money(_ga.Price), new BuyerInfo("An", "an@example.com", null), now.AddMinutes(8), now);
            var created = await AddAttemptAsync(db, order, now);
            order.Cancel();
            await db.SaveChangesAsync(Ct);
            return created;
        });

        (await OutcomeAsync(await SucceedAsync(placed))).ShouldBe("refund_pending");

        var state = await StateAsync(placed.OrderId);
        state.Status.ShouldBe(OrderStatus.Cancelled);
        state.Tickets.ShouldBe(0);
        state.Attempts.ShouldBe([PaymentAttemptStatus.RefundPending]);
        state.Outbox.ShouldBe([nameof(OrderCancelled), nameof(PaymentRefundRequested)]);
    }

    [Fact]
    public async Task A_success_for_a_different_amount_issues_nothing_and_refunds_what_was_charged()
    {
        var placed = await PlaceAsync(Ga(2));

        (await OutcomeAsync(await DeliverAsync(Body(placed.SessionId, amount: 1_000)))).ShouldBe("refund_pending");

        var state = await StateAsync(placed.OrderId);
        state.Status.ShouldBe(OrderStatus.AwaitingPayment);
        state.Tickets.ShouldBe(0);
        state.Attempts.ShouldBe([PaymentAttemptStatus.RefundPending]);
        var refund = await WithDbAsync(db => db.OutboxMessages.AsNoTracking().SingleAsync(m => m.Type == nameof(PaymentRefundRequested), Ct));
        var payload = JsonSerializer.Deserialize<PaymentRefundRequested>(refund.Payload, Json)!;
        payload.Amount.ShouldBe(1_000);
        payload.Reason.ShouldBe(RefundReasons.AmountMismatch);
        payload.AttemptId.ShouldBe(placed.AttemptId);
    }

    // ================================================================ failures and races

    /// <summary>
    /// A host whose purchase-limit check runs hooks inside the fulfilment transaction: <paramref name="before"/> after the
    /// event sale lock and before the buyer lock, <paramref name="after"/> once the buyer lock is held and the count is done
    /// (numbered in the order the lock was won). Barrier tests hold a transaction there, so the host's lock timeout is
    /// generous unless <paramref name="lockTimeout"/> says otherwise.
    /// </summary>
    private (WebApplicationFactory<Program> Host, HttpClient Client) HookedHost(
        Func<IServiceProvider, int, Task>? before = null, Func<IServiceProvider, int, Task>? after = null, string lockTimeout = "00:00:30")
    {
        var beforeCalls = 0;
        var afterCalls = 0;
        var host = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Fulfilment:LockTimeout", lockTimeout);
            b.ConfigureTestServices(s => s.AddScoped<ITicketPurchaseLimit>(sp => new HookedLimit(
                ActivatorUtilities.CreateInstance<TicketPurchaseLimit>(sp),
                () => before?.Invoke(sp, Interlocked.Increment(ref beforeCalls)) ?? Task.CompletedTask,
                () => after?.Invoke(sp, Interlocked.Increment(ref afterCalls)) ?? Task.CompletedTask)));
        });
        return (host, host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false }));
    }

    /// <summary>Waits until <paramref name="count"/> sessions of this test's database wait for a lock of the given kind (pg_stat_activity).</summary>
    private async Task WaitForLockWaitersAsync(string waitEvent, int count)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (await WithDbAsync(db => db.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM pg_stat_activity
            WHERE datname = current_database() AND wait_event_type = 'Lock' AND wait_event = {waitEvent}
            """).SingleAsync(Ct)) < count)
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new ShouldAssertException($"Expected {count} sessions waiting on a {waitEvent} lock.");
            }

            await Task.Delay(25, Ct);
        }
    }

    [Fact]
    public async Task A_database_failure_mid_transaction_is_5xx_commits_nothing_and_the_redelivery_succeeds()
    {
        var placed = await PlaceAsync(Seat(_seats[7]), Ga(1));
        var (host, client) = HookedHost(async (sp, call) =>
        {
            if (call == 1)
            {
                // Kills this request's own database session: the provider must see a 5xx and retry.
                await sp.GetRequiredService<AppDbContext>().Database.ExecuteSqlRawAsync("SELECT pg_terminate_backend(pg_backend_pid())", Ct);
            }
        });
        await using var hostLease = host;
        using var clientLease = client;
        var body = Body(placed.SessionId, amount: placed.Total);
        var soldBefore = await SoldCountAsync();

        var failed = await DeliverAsync(body, client);

        ((int)failed.StatusCode).ShouldBeGreaterThanOrEqualTo(500);
        (await WithDbAsync(db => db.ProcessedWebhooks.CountAsync(Ct))).ShouldBe(0);
        (await StateAsync(placed.OrderId)).ShouldBe(new OrderState(OrderStatus.AwaitingPayment, null, [PaymentAttemptStatus.Pending], 0, []), new OrderStateComparer());
        (await SoldCountAsync()).ShouldBe(soldBefore);

        (await OutcomeAsync(await DeliverAsync(body, client))).ShouldBe("paid");
        (await StateAsync(placed.OrderId)).Tickets.ShouldBe(2);
    }

    [Fact]
    public async Task The_expiry_sweep_winning_the_race_mid_fulfilment_is_retried_and_the_late_success_still_pays()
    {
        var placed = await PlaceAsync(Seat(_seats[8]), Ga(1));
        var (host, client) = HookedHost(async (_, call) =>
        {
            if (call == 1)
            {
                // The sweep expires the order after this webhook loaded it and before it saves (xmin conflict).
                await ChangeOrderAsync(placed.OrderId, o => o.Expire());
            }
        });
        await using var hostLease = host;
        using var clientLease = client;

        (await OutcomeAsync(await SucceedAsync(placed, client: client))).ShouldBe("paid");

        var state = await StateAsync(placed.OrderId);
        state.Status.ShouldBe(OrderStatus.Paid);
        state.Tickets.ShouldBe(2);
        state.Outbox.ShouldBe([nameof(OrderExpired), nameof(OrderPaid)]);
        (await WithDbAsync(db => db.ProcessedWebhooks.CountAsync(Ct))).ShouldBe(1);
        await AssertInventoryInvariantsAsync();
    }

    [Fact]
    public async Task A_concurrent_change_on_every_try_gives_up_with_5xx_and_commits_nothing()
    {
        var placed = await PlaceAsync(Ga(1));
        var (host, client) = HookedHost((_, _) => WithDbAsync(db =>
            db.Database.ExecuteSqlAsync($"UPDATE orders SET updated_at = now() WHERE id = {placed.OrderId}", Ct)));
        await using var hostLease = host;
        using var clientLease = client;
        var soldBefore = await SoldCountAsync();

        var response = await SucceedAsync(placed, client: client);

        ((int)response.StatusCode).ShouldBeGreaterThanOrEqualTo(500);
        var state = await StateAsync(placed.OrderId);
        state.Status.ShouldBe(OrderStatus.AwaitingPayment);
        state.Attempts.ShouldBe([PaymentAttemptStatus.Pending]);
        // The final save wrote the GA tickets and the OrderPaid outbox row in the same batch as the failing order update:
        // the rollback took them all, so no consumer can ever see an OrderPaid for an unpaid order.
        state.Tickets.ShouldBe(0);
        state.Outbox.ShouldBeEmpty();
        (await WithDbAsync(db => db.ProcessedWebhooks.CountAsync(Ct))).ShouldBe(0);
        (await SoldCountAsync()).ShouldBe(soldBefore);
    }

    [Fact]
    public async Task After_a_burst_of_competing_late_payments_the_sql_invariants_hold()
    {
        await WithDbAsync(db => db.Database.ExecuteSqlAsync($"UPDATE ticket_tiers SET capacity = sold_count + 6 WHERE id = {_ga.Id}", Ct));
        var random = new Random(20261106);
        var pool = _seats.Take(6).ToArray();
        var orders = new List<(Placed Placed, int Quantity)>();
        for (var i = 0; i < 16; i++)
        {
            var seats = pool.OrderBy(_ => random.Next()).Take(random.Next(0, 3)).Select(Seat).ToArray();
            var ga = random.Next(seats.Length > 0 ? 0 : 1, 3);
            var items = seats.Concat(ga > 0 ? [Ga(ga)] : []).ToArray();
            orders.Add((await PlaceAsync(items), items.Sum(item => item.Quantity)));
        }

        var bodies = orders.Select(o => Body(o.Placed.SessionId, amount: o.Placed.Total)).ToList();
        var deliveries = bodies.Concat(bodies.Take(5)).OrderBy(_ => random.Next()).ToList();
        await Task.WhenAll(deliveries.Select(DeliverUntilAcceptedAsync));

        await AssertInventoryInvariantsAsync();
        foreach (var (placed, quantity) in orders)
        {
            var state = await StateAsync(placed.OrderId);
            state.Status.ShouldBeOneOf(OrderStatus.Paid, OrderStatus.RefundPending);
            state.Tickets.ShouldBe(state.Status == OrderStatus.Paid ? quantity : 0);
            state.Outbox.Count(t => t is nameof(OrderPaid) or nameof(RefundRequested)).ShouldBe(1);
        }

        (await WithDbAsync(db => db.ProcessedWebhooks.CountAsync(Ct))).ShouldBe(orders.Count);
    }

    /// <summary>Like a provider: a 5xx is retried, a 200 ends the delivery.</summary>
    private async Task DeliverUntilAcceptedAsync(string body)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var response = await DeliverAsync(body);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                return;
            }

            ((int)response.StatusCode).ShouldBeGreaterThanOrEqualTo(500);
        }

        throw new ShouldAssertException("A webhook was never accepted.");
    }

    // ================================================================ races with barriers (fix round 1)

    [Fact]
    public async Task Two_orders_of_one_buyer_over_the_limit_together_racing_fulfil_one_and_refund_the_other()
    {
        var buyer = Guid.CreateVersion7();
        var first = await PlaceForAsync(buyer, Ga(3));
        var second = await PlaceForAsync(buyer, Ga(2));
        // Barrier: the order that wins the buyer lock holds it, count done, until the other is blocked on that lock.
        var (host, client) = HookedHost(after: async (_, call) =>
        {
            if (call == 1)
            {
                await WaitForLockWaitersAsync("advisory", 1);
            }
        });
        await using var hostLease = host;
        using var clientLease = client;

        var outcomes = await Task.WhenAll(new[] { first, second }.Select(async p => await OutcomeAsync(await SucceedAsync(p, client: client))));

        outcomes.Order().ShouldBe(["paid", "refund_pending"]);
        var states = new[] { await StateAsync(first.OrderId), await StateAsync(second.OrderId) };
        var refunded = states.Single(s => s.Status == OrderStatus.RefundPending);
        refunded.RefundReason.ShouldBe(RefundReasons.PurchaseLimitExceeded);
        refunded.Tickets.ShouldBe(0);
        (await WithDbAsync(db => db.Tickets.CountAsync(t => t.OwnerUserId == buyer, Ct))).ShouldBeLessThanOrEqualTo(4);
        await AssertInventoryInvariantsAsync();
    }

    [Fact]
    public async Task Two_event_ids_for_one_session_racing_pay_once_and_ignore_the_other()
    {
        var placed = await PlaceAsync(Seat(_seats[10]), Ga(1));
        var (host, client) = HookedHost(after: async (_, call) =>
        {
            if (call == 1)
            {
                await WaitForLockWaitersAsync("advisory", 1);
            }
        });
        await using var hostLease = host;
        using var clientLease = client;

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ => await OutcomeAsync(await SucceedAsync(placed, client: client))));

        outcomes.Order().ShouldBe(["ignored", "paid"]);
        var state = await StateAsync(placed.OrderId);
        state.Status.ShouldBe(OrderStatus.Paid);
        state.Tickets.ShouldBe(2);
        state.Attempts.ShouldBe([PaymentAttemptStatus.Succeeded]);
        state.Outbox.Count(t => t == nameof(OrderPaid)).ShouldBe(1);
        (await WithDbAsync(db => db.ProcessedWebhooks.CountAsync(Ct))).ShouldBe(2);
    }

    private Task<int> CancelEventLikeB9Async() => WithDbAsync(async db =>
    {
        await using var transaction = await db.Database.BeginTransactionAsync(Ct);
        await EventSaleLocks.AcquireExclusiveAsync(db, _event, Ct);
        await db.Database.ExecuteSqlAsync($"UPDATE events SET status = 'Cancelled' WHERE id = {_event}", Ct);
        await transaction.CommitAsync(Ct);
        return 0;
    });

    private Task<EventStatus> EventStatusAsync() =>
        WithDbAsync(db => db.Events.AsNoTracking().Where(e => e.Id == _event).Select(e => e.Status).SingleAsync(Ct));

    [Fact]
    public async Task A_cancellation_that_starts_during_a_fulfilment_waits_for_it_and_the_order_is_paid()
    {
        var placed = await PlaceAsync(Seat(_seats[11]), Ga(1));
        Task? cancel = null;
        var (host, client) = HookedHost(before: async (_, call) =>
        {
            if (call == 1)
            {
                // The fulfilment holds the shared sale lock; the cancellation must queue behind it.
                cancel = Task.Run(CancelEventLikeB9Async);
                await WaitForLockWaitersAsync("advisory", 1);
                cancel.IsCompleted.ShouldBeFalse();
            }
        });
        await using var hostLease = host;
        using var clientLease = client;

        (await OutcomeAsync(await SucceedAsync(placed, client: client))).ShouldBe("paid");
        await cancel!;

        (await EventStatusAsync()).ShouldBe(EventStatus.Cancelled);
        var state = await StateAsync(placed.OrderId);
        state.Status.ShouldBe(OrderStatus.Paid, "paid before the cancel committed: B9's sweep refunds it");
        state.Tickets.ShouldBe(2);
    }

    [Fact]
    public async Task A_fulfilment_that_starts_during_a_cancellation_waits_for_it_and_sees_the_cancel()
    {
        var placed = await PlaceAsync(Seat(_seats[12]), Ga(1));
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(Ct);
        await EventSaleLocks.AcquireExclusiveAsync(db, _event, Ct);
        await db.Database.ExecuteSqlAsync($"UPDATE events SET status = 'Cancelled' WHERE id = {_event}", Ct);

        var webhook = SucceedAsync(placed);
        await WaitForLockWaitersAsync("advisory", 1);
        webhook.IsCompleted.ShouldBeFalse("the fulfilment must wait for the exclusive sale lock");
        await transaction.CommitAsync(Ct);

        (await OutcomeAsync(await webhook)).ShouldBe("refund_pending");
        var state = await StateAsync(placed.OrderId);
        state.Status.ShouldBe(OrderStatus.RefundPending);
        state.RefundReason.ShouldBe(RefundReasons.EventCancelled);
        state.Tickets.ShouldBe(0);
    }

    [Fact]
    public async Task A_sale_lock_held_past_the_lock_timeout_is_a_5xx_that_commits_nothing_and_the_redelivery_succeeds()
    {
        var placed = await PlaceAsync(Ga(1));
        var (host, client) = HookedHost(lockTimeout: "00:00:00.500");
        await using var hostLease = host;
        using var clientLease = client;
        var body = Body(placed.SessionId, amount: placed.Total);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await using var stuck = await db.Database.BeginTransactionAsync(Ct);
            await EventSaleLocks.AcquireExclusiveAsync(db, _event, Ct);

            await ShouldBeProblemAsync(await DeliverAsync(body, client), HttpStatusCode.InternalServerError, "webhook.not_processed");

            (await WithDbAsync(d => d.ProcessedWebhooks.CountAsync(Ct))).ShouldBe(0);
            (await StateAsync(placed.OrderId)).Status.ShouldBe(OrderStatus.AwaitingPayment);
        }

        (await OutcomeAsync(await DeliverAsync(body, client))).ShouldBe("paid");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_deadlock_is_retried_up_to_three_times_then_a_clean_5xx(bool always)
    {
        var placed = await PlaceAsync(Ga(1));
        var calls = 0;
        var (host, client) = HookedHost(before: (_, call) =>
        {
            Interlocked.Exchange(ref calls, call);
            return always || call == 1 ? Task.FromException(new Npgsql.PostgresException("deadlock detected", "ERROR", "ERROR", "40P01")) : Task.CompletedTask;
        });
        await using var hostLease = host;
        using var clientLease = client;
        var soldBefore = await SoldCountAsync();

        var response = await SucceedAsync(placed, client: client);

        if (!always)
        {
            (await OutcomeAsync(response)).ShouldBe("paid");
            calls.ShouldBe(2);
            return;
        }

        await ShouldBeProblemAsync(response, HttpStatusCode.InternalServerError, "webhook.not_processed");
        calls.ShouldBe(1 + 3);
        (await StateAsync(placed.OrderId)).ShouldBe(new OrderState(OrderStatus.AwaitingPayment, null, [PaymentAttemptStatus.Pending], 0, []), new OrderStateComparer());
        (await WithDbAsync(db => db.ProcessedWebhooks.CountAsync(Ct))).ShouldBe(0);
        (await SoldCountAsync()).ShouldBe(soldBefore);
    }

    [Fact]
    public async Task A_conflict_without_a_reason_is_a_bug_answered_5xx_and_never_refunded()
    {
        var placed = await PlaceAsync(Ga(1));
        var calls = 0;
        var (host, client) = HookedHost(before: (_, call) =>
        {
            Interlocked.Exchange(ref calls, call);
            return Task.FromException(new InventoryConflictException("a strategy forgot to say why"));
        });
        await using var hostLease = host;
        using var clientLease = client;

        await ShouldBeProblemAsync(await SucceedAsync(placed, client: client), HttpStatusCode.InternalServerError, "webhook.not_processed");

        calls.ShouldBe(1);
        (await StateAsync(placed.OrderId)).ShouldBe(new OrderState(OrderStatus.AwaitingPayment, null, [PaymentAttemptStatus.Pending], 0, []), new OrderStateComparer());
        (await WithDbAsync(db => db.ProcessedWebhooks.CountAsync(Ct))).ShouldBe(0);
    }

    // ================================================================ remaining decision-table cells (fix round 1)

    /// <summary>An order with one Pending attempt (one GA ticket), moved by <paramref name="then"/> into the state a cell needs.</summary>
    private Task<Placed> PlaceWithAsync(bool started, Action<Order> then) => WithDbAsync(async db =>
    {
        var now = DateTimeOffset.UtcNow;
        var order = Order.Create(Guid.CreateVersion7(), _event, Guid.CreateVersion7(), [Ga(1)], new Money(_ga.Price), new BuyerInfo("An", "an@example.com", null), now.AddMinutes(8), now);
        if (started)
        {
            order.StartPayment(now);
        }

        var placed = await AddAttemptAsync(db, order, now);
        then(order);
        await db.SaveChangesAsync(Ct);
        return placed;
    });

    [Theory]
    [InlineData(OrderStatus.RefundPending, RefundReasons.DuplicatePayment)]
    [InlineData(OrderStatus.Refunded, RefundReasons.DuplicatePayment)]
    [InlineData(OrderStatus.Pending, RefundReasons.OrderClosed)]
    public async Task A_success_on_an_order_that_cannot_take_this_money_refunds_the_session_only(OrderStatus status, string reason)
    {
        var placed = await PlaceWithAsync(status != OrderStatus.Pending, order =>
        {
            if (status != OrderStatus.Pending)
            {
                order.MarkUnfulfillable();
            }

            if (status == OrderStatus.Refunded)
            {
                order.MarkRefunded();
            }
        });

        (await OutcomeAsync(await SucceedAsync(placed))).ShouldBe("refund_pending");

        var state = await StateAsync(placed.OrderId);
        state.Status.ShouldBe(status);
        state.Tickets.ShouldBe(0);
        state.Attempts.ShouldBe([PaymentAttemptStatus.RefundPending]);
        var refund = await WithDbAsync(db => db.OutboxMessages.AsNoTracking().SingleAsync(m => m.Type == nameof(PaymentRefundRequested), Ct));
        JsonSerializer.Deserialize<PaymentRefundRequested>(refund.Payload, Json)!.Reason.ShouldBe(reason);
    }

    [Theory]
    [InlineData(OrderStatus.Expired)]
    [InlineData(OrderStatus.PaymentFailed)]
    [InlineData(OrderStatus.Paid)]
    public async Task A_failure_of_a_pending_attempt_on_an_order_that_is_not_awaiting_only_fails_the_attempt(OrderStatus status)
    {
        var placed = await PlaceWithAsync(true, order =>
        {
            switch (status)
            {
                case OrderStatus.Expired:
                    order.Expire();
                    break;
                case OrderStatus.PaymentFailed:
                    order.MarkPaymentFailed();
                    break;
                default:
                    order.MarkPaid(DateTimeOffset.UtcNow);
                    break;
            }
        });

        (await OutcomeAsync(await DeliverAsync(Body(placed.SessionId, MockWebhookPayload.FailedType, placed.Total)))).ShouldBe("failed");

        var state = await StateAsync(placed.OrderId);
        state.Status.ShouldBe(status);
        state.Attempts.ShouldBe([PaymentAttemptStatus.Failed]);
    }

    [Fact]
    public async Task A_refund_confirmation_on_a_paid_order_is_ignored()
    {
        var placed = await PlaceAsync(Ga(1));
        (await OutcomeAsync(await SucceedAsync(placed))).ShouldBe("paid");

        (await OutcomeAsync(await DeliverAsync(Body(placed.SessionId, MockWebhookPayload.RefundedType, placed.Total)))).ShouldBe("ignored");

        var state = await StateAsync(placed.OrderId);
        state.Status.ShouldBe(OrderStatus.Paid);
        state.Tickets.ShouldBe(1);
        state.Attempts.ShouldBe([PaymentAttemptStatus.Succeeded]);
    }

    /// <summary>
    /// An order whose attempt is backed by a real mock-gateway session that the buyer has paid, so a requested refund can be
    /// executed against the gateway exactly as B8's refund worker will.
    /// </summary>
    private Task<Placed> PlaceWithPaidMockSessionAsync(params OrderItem[] items) => WithDbAsync(async db =>
    {
        var now = DateTimeOffset.UtcNow;
        var total = items.Aggregate(Money.Zero(), (sum, item) => sum + item.LineTotal());
        var order = Order.Create(Guid.CreateVersion7(), _event, Guid.CreateVersion7(), items, total, new BuyerInfo("An", "an@example.com", null), now.AddMinutes(8), now);
        order.StartPayment(now);
        var gateway = _factory.Services.GetRequiredService<MockPaymentGateway>();
        var session = await gateway.CreateSessionAsync(
            order, new Uri($"http://localhost:3000/checkout/{order.Id}/result"), order.ExpiresAt - PaymentAttempt.GatewayMargin, $"order:{order.Id:N}:attempt:1", Ct);
        (await _factory.Services.GetRequiredService<MockGatewayControl>().CompleteAsync(session.SessionId, MockOutcome.Succeeded, Ct)).IsSuccess.ShouldBeTrue();
        var attempt = PaymentAttempt.Create(order.Id, "mock", session.SessionId, session.RedirectUrl.AbsoluteUri, total, order.ExpiresAt, now);
        db.Orders.Add(order);
        db.PaymentAttempts.Add(attempt);
        await db.SaveChangesAsync(Ct);
        return new Placed(order.Id, attempt.Id, session.SessionId, total.Amount, order.UserId);
    });

    /// <summary>
    /// The refund must be one the gateway can execute: in the session's currency and never more than the session collected.
    /// A smaller same-currency amount is refunded as reported (that is what was captured).
    /// </summary>
    [Theory]
    [InlineData("missing_amount")]
    [InlineData("other_currency")]
    [InlineData("more_than_collected")]
    [InlineData("less_than_collected")]
    [InlineData("zero_amount")]
    public async Task A_success_without_the_sessions_exact_amount_mints_nothing_and_requests_a_refund_the_gateway_accepts(string kind)
    {
        var placed = await PlaceWithPaidMockSessionAsync(Ga(2));
        var (body, expected) = kind switch
        {
            "missing_amount" => (Body(placed.SessionId), placed.Total),
            "zero_amount" => (Body(placed.SessionId, amount: 0), placed.Total),
            "other_currency" => (Body(placed.SessionId, amount: placed.Total, currency: "USD"), placed.Total),
            "more_than_collected" => (Body(placed.SessionId, amount: placed.Total + 500_000), placed.Total),
            _ => (Body(placed.SessionId, amount: placed.Total - 1_000), placed.Total - 1_000),
        };

        (await OutcomeAsync(await DeliverAsync(body))).ShouldBe("refund_pending");

        var state = await StateAsync(placed.OrderId);
        state.Status.ShouldBe(OrderStatus.AwaitingPayment);
        state.Tickets.ShouldBe(0);
        state.Attempts.ShouldBe([PaymentAttemptStatus.RefundPending]);
        var refund = await WithDbAsync(db => db.OutboxMessages.AsNoTracking().SingleAsync(m => m.Type == nameof(PaymentRefundRequested), Ct));
        var payload = JsonSerializer.Deserialize<PaymentRefundRequested>(refund.Payload, Json)!;
        payload.Reason.ShouldBe(RefundReasons.AmountMismatch);
        payload.Amount.ShouldBe(expected);
        payload.Currency.ShouldBe("VND");
        var executed = await _factory.Services.GetRequiredService<MockPaymentGateway>()
            .RefundAsync(payload.SessionId, new Money(payload.Amount, payload.Currency), $"attempt:{payload.AttemptId}", Ct);
        executed.IsSuccess.ShouldBeTrue(executed.FailureReason);
    }

    [Fact]
    public async Task An_event_id_longer_than_200_characters_is_400_and_changes_nothing()
    {
        var placed = await PlaceAsync(Ga(1));
        var before = await TotalsAsync();

        var response = await DeliverAsync(Body(placed.SessionId, amount: placed.Total, eventId: new string('e', 201)));

        await ShouldBeProblemAsync(response, HttpStatusCode.BadRequest, "webhook.invalid_payload");
        (await TotalsAsync()).ShouldBe(before);
    }

    // ================================================================ test doubles

    private sealed class HookedLimit(ITicketPurchaseLimit inner, Func<Task> before, Func<Task> after) : ITicketPurchaseLimit
    {
        public async Task<bool> AllowsAsync(Guid eventId, Guid userId, int adding, CancellationToken ct)
        {
            await before();
            var allowed = await inner.AllowsAsync(eventId, userId, adding, ct);
            await after();
            return allowed;
        }
    }

    /// <summary>Content without a Content-Length, so the server must bound the read itself.</summary>
    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class BytesComparer : IEqualityComparer<byte[]>
    {
        public bool Equals(byte[]? x, byte[]? y) => x.AsSpan().SequenceEqual(y);

        public int GetHashCode(byte[] obj) => obj.Length == 0 ? 0 : obj[0];
    }

    private sealed class OrderStateComparer : IEqualityComparer<OrderState>
    {
        public bool Equals(OrderState? x, OrderState? y) =>
            x is not null && y is not null && x.Status == y.Status && x.RefundReason == y.RefundReason && x.Tickets == y.Tickets
            && x.Attempts.SequenceEqual(y.Attempts) && x.Outbox.SequenceEqual(y.Outbox);

        public int GetHashCode(OrderState obj) => obj.Status.GetHashCode();
    }
}
