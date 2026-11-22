using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Application.Abstractions.Identity;
using TicketRush.Application.Abstractions.Inventory;
using TicketRush.Application.Features.Orders;
using TicketRush.Domain.Events;
using TicketRush.Domain.Orders;
using TicketRush.Domain.Primitives;
using TicketRush.Domain.Tickets;
using TicketRush.Infrastructure.Payments;

namespace TicketRush.Api.IntegrationTests.Orders;

/// <summary>
/// Spec 08 acceptance #8 as an automated test: a concurrent mini-sale through the real HTTP pipeline (hold in Redis, then
/// <c>POST /orders</c> and <c>/pay</c> with double-clicks, the mock gateway deciding, signed webhooks delivered over HTTP by
/// several runners at once, duplicates and late ones, the expiry sweeper and the refund worker), then
/// <c>load-tests/check-invariants.sql</c> (the SAME query the k6 run is checked with) must return zero rows.
/// <para>
/// The sale is built to collide: eight hot seats with four buyers each, a general-admission tier of 6 with 15+ tickets of
/// demand, and "delayed" payers who free their hold (as an expired hold would) so a second buyer takes the seat before the
/// late payment arrives. Whichever of the two commits second is refunded: never two tickets for one seat, never over capacity.
/// </para>
/// </summary>
public sealed class SaleInvariantTests(ContainersFixture containers) : IAsyncLifetime
{
    private const int GaCapacity = 6;
    private const int HotSeats = 8;
    private const int SeatBuyers = 32;
    private const int GaBuyers = 8;

    /// <summary>What the buyer does at the gateway page; the seat outcome is chosen by seat, the GA one by buyer.</summary>
    private enum Gw
    {
        Succeeded,
        Failed,
        Delayed,
        Twice,
        Abandon,
        SucceededThenFailed,
    }

    private static readonly Gw[] SeatOutcomes = [Gw.Succeeded, Gw.Delayed, Gw.Failed, Gw.Twice, Gw.Delayed, Gw.Abandon, Gw.SucceededThenFailed, Gw.Delayed];
    private static readonly Gw[] GaOutcomes = [Gw.Succeeded, Gw.Delayed, Gw.Succeeded, Gw.Delayed, Gw.Twice, Gw.Failed, Gw.Delayed, Gw.Succeeded];
    private static readonly Gw[] SecondChance = [Gw.Succeeded, Gw.Delayed, Gw.Twice];

    private readonly SemaphoreSlim _gate = new(12);
    private readonly List<string> _failedAfterSuccess = [];
    private CheckoutWorkersHarness _h = null!;
    private HttpClient _http = null!;
    private HttpClient _webhookHttp = null!;
    private Guid[] _gaTiers = [];

    private sealed record Buyer(Guid UserId, string Token);

    private sealed record Want(int? Seat, int GaQuantity, Gw Outcome);

    private sealed record Bought(Guid OrderId, string SessionId, Guid HoldId);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IInventoryStore Store => _h.Host.Services.GetRequiredService<IInventoryStore>();

    private MockWebhookQueue Queue => _h.Host.Services.GetRequiredService<MockWebhookQueue>();

    public async ValueTask InitializeAsync()
    {
        _h = await CheckoutWorkersHarness.CreateAsync(containers, new Dictionary<string, string?>
        {
            // A failed delivery is retried a millisecond later, so the fake clock only has to move a little.
            ["Payments:Mock:DeliveryRetryBaseDelay"] = "00:00:00.001",
            // Tokens are minted on the fake clock but checked against the real one; a long life keeps a slow machine from outliving them.
            ["Jwt:AccessTokenMinutes"] = "60",
        });
        _http = _h.Host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        _webhookHttp = new HttpClient(_h.Host.Server.CreateHandler());
        await _h.WithDbAsync(db => db.Database.ExecuteSqlAsync($"UPDATE ticket_tiers SET capacity = {GaCapacity} WHERE id = {_h.Ga.Id}", Ct));
        await using var scope = _h.Host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IInventorySeeder>().SeedAsync(_h.EventId, Ct);
        _gaTiers = await _h.WithDbAsync(async db => (await db.Events.AsNoTracking().Include(e => e.Tiers).SingleAsync(e => e.Id == _h.EventId, Ct))
            .Tiers.Where(t => t.Kind == TicketKind.GeneralAdmission).Select(t => t.Id).ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        _webhookHttp.Dispose();
        _gate.Dispose();
        await _h.DisposeAsync();
    }

    [Fact(Timeout = 300_000)]
    public async Task A_concurrent_sale_with_contention_duplicates_and_late_payments_never_oversells()
    {
        // The helpers use the same token, so the 5 minute timeout cancels every call in flight.
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var buyers = Enumerable.Range(0, SeatBuyers + GaBuyers).Select(_ => NewBuyer()).ToArray();
        var wants = Enumerable.Range(0, SeatBuyers).Select(i => new Want(_h.Seats[i % HotSeats], 0, SeatOutcomes[i % HotSeats]))
            .Concat(Enumerable.Range(0, GaBuyers).Select(j => new Want(null, (j % 3) + 1, GaOutcomes[j])))
            .ToArray();

        // Wave 1: everybody at once.
        var first = await Task.WhenAll(buyers.Zip(wants, (buyer, want) => AttemptAsync(buyer, want)));
        await DeliverAsync();
        foreach (var session in _failedAfterSuccess)
        {
            await _h.DeliverFailedAsync(new CheckoutWorkersHarness.Placed(Guid.Empty, Guid.Empty, session, Money.Zero(), Guid.Empty));
        }

        // Wave 2, four minutes later: the buyers who lost a race try again while the delayed webhooks of wave 1 arrive.
        _h.Time.Advance(TimeSpan.FromMinutes(4));
        var retries = buyers.Zip(wants, first).Where(x => x.Third is null).ToArray();
        retries.Length.ShouldBeGreaterThan(0, "the sale must have collided");
        var second = Task.WhenAll(retries.Select((x, k) => AttemptAsync(x.First, x.Second with { Outcome = SecondChance[k % SecondChance.Length] })));
        var delivering = DeliverWhileAsync(second);
        await Task.WhenAll(second, delivering);

        await SettleAsync();

        await AssertSaleIsConsistentAsync();
    }

    [Fact]
    public async Task The_invariant_query_reports_every_kind_of_planted_corruption()
    {
        var clean = await PaidSeatOrderAsync(_h.Seats[0]);
        var duplicated = await PaidSeatOrderAsync(_h.Seats[1]);
        var emptied = await PaidSeatOrderAsync(_h.Seats[2]);
        var unpaid = await PaidGaOrderAsync(2);
        var refunding = await PaidGaOrderAsync(1);
        var wrongTier = await PaidGaOrderAsync(2);
        (await CheckAsync()).ShouldBeEmpty();
        var cleanTicket = await _h.WithDbAsync(db => db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM tickets WHERE order_id = {clean.OrderId}").SingleAsync(Ct));

        await Corrupt($"DELETE FROM tickets WHERE order_id = {emptied.OrderId}");
        await Corrupt($"UPDATE orders SET status = 'Expired' WHERE id = {unpaid.OrderId}");
        await Corrupt($"UPDATE orders SET status = 'RefundPending' WHERE id = {refunding.OrderId}");
        await Corrupt($"UPDATE tickets SET event_id = (SELECT id FROM events WHERE id <> {_h.EventId} LIMIT 1) WHERE order_id = {clean.OrderId}");
        // The right number of tickets in total (2), but one of them in the wrong tier: only the per-tier check can see it.
        await Corrupt($"UPDATE tickets SET tier_id = {_h.Seated.Id} WHERE id = (SELECT id FROM tickets WHERE order_id = {wrongTier.OrderId} LIMIT 1)");
        await Corrupt($"UPDATE ticket_tiers SET sold_count = capacity + 1 WHERE id = {_h.Ga.Id}");
        // Five live tickets of the event end up with one user, over the event's limit of 4 (spec 17).
        var hoarder = Guid.CreateVersion7();
        await Corrupt($"UPDATE tickets SET owner_user_id = {hoarder} WHERE order_id IN ({unpaid.OrderId}, {wrongTier.OrderId}, {refunding.OrderId})");
        await _h.WithDbAsync(db => db.Database.ExecuteSqlRawAsync(DropTicketSeatIndex, Ct));
        await Corrupt($"""
            INSERT INTO tickets (id, order_id, event_id, tier_id, seat_index, owner_user_id, status, qr_secret, created_at, updated_at)
            SELECT gen_random_uuid(), order_id, event_id, tier_id, seat_index, owner_user_id, status, qr_secret, created_at, updated_at
            FROM tickets WHERE seat_index = {_h.Seats[1]}
            """);

        var found = await CheckAsync();
        SubjectsOf(found, "seat_sold_twice").ShouldBe([$"{_h.EventId}/seat {_h.Seats[1]}"]);
        SubjectsOf(found, "tier_oversold").ShouldBe([_h.Ga.Id.ToString()]);
        SubjectsOf(found, "ga_sold_count_drift").ShouldBe([_h.Ga.Id.ToString()]);
        SubjectsOf(found, "ticket_event_mismatch").ShouldBe([cleanTicket.ToString()]);
        SubjectsOf(found, "unpaid_order_has_tickets").ShouldBe([unpaid.OrderId.ToString()]);
        SubjectsOf(found, "refund_order_has_tickets").ShouldBe([refunding.OrderId.ToString()]);
        // Wrong total: the order without tickets and the order with a duplicated ticket, NOT the one with a wrong-tier ticket.
        SubjectsOf(found, "paid_order_ticket_count").ShouldBe([duplicated.OrderId.ToString(), emptied.OrderId.ToString()], ignoreOrder: true);
        SubjectsOf(found, "paid_order_tier_tickets").Select(x => x.Split('/')[0]).ShouldBe(
            [duplicated.OrderId.ToString(), emptied.OrderId.ToString(), wrongTier.OrderId.ToString()], ignoreOrder: true);
        SubjectsOf(found, "user_over_ticket_limit").ShouldBe([$"{_h.EventId}/user {hoarder}"]);
        found.Select(v => v.Check).Distinct().Count().ShouldBe(9, "no other check fires");
    }

    private static string[] SubjectsOf(IReadOnlyList<SaleInvariants.Violation> found, string check) =>
        [.. found.Where(v => v.Check == check).Select(v => v.Subject)];

    // ---------------------------------------------------------------- the sale

    private const string DropTicketSeatIndex = "DROP INDEX ux_tickets_event_seat";

    private Buyer NewBuyer()
    {
        var id = Guid.CreateVersion7();
        using var scope = _h.Host.Services.CreateScope();
        // Minted up front: the api validates tokens against the real clock, which the fake one must not run ahead of.
        var token = scope.ServiceProvider.GetRequiredService<IAccessTokenIssuer>()
            .Issue(new UserAccount(id, $"buyer-{id:N}@example.com", "Khách hàng", null, null, true, ["Customer"])).Token;
        return new Buyer(id, token);
    }

    /// <summary>Hold, order, pay, gateway. Null when the buyer lost the race for the seats or the tickets.</summary>
    private async Task<Bought?> AttemptAsync(Buyer buyer, Want want)
    {
        await _gate.WaitAsync(Ct);
        try
        {
            var hold = await TryHoldAsync(buyer, want);
            if (hold is null)
            {
                return null;
            }

            var bought = await CheckOutAsync(buyer, hold.Value);
            await FinishAtGatewayAsync(bought, want.Outcome);
            return bought;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Guid?> TryHoldAsync(Buyer buyer, Want want)
    {
        var request = new HoldRequest(
            _h.EventId, buyer.UserId, want.Seat is { } seat ? [seat] : [], want.GaQuantity > 0 ? [new GaLine(_h.Ga.Id, want.GaQuantity)] : [],
            _gaTiers, 4, false, _h.Time.GetUtcNow(), TimeSpan.FromMinutes(8));
        var outcome = await Store.CreateHoldAsync(request, Ct);
        outcome.Status.ShouldBeOneOf(HoldStatus.Ok, HoldStatus.SeatUnavailable, HoldStatus.GaInsufficient);
        return outcome.Status == HoldStatus.Ok ? outcome.HoldId : null;
    }

    private async Task<Bought> CheckOutAsync(Buyer buyer, Guid holdId)
    {
        var order = await PostTwiceAsync(buyer, "/api/v1/orders", new { eventId = _h.EventId, holdId, buyer = new { name = "Nguyễn Văn An", email = "an@example.com", phone = "0901234567" } }, "id");
        var orderId = order.GetProperty("id").GetGuid();
        var pay = await PostTwiceAsync(buyer, $"/api/v1/orders/{orderId}/pay", null, "redirectUrl");
        var session = new Uri(pay.GetProperty("redirectUrl").GetString()!).Segments[^1];
        return new Bought(orderId, session, holdId);
    }

    /// <summary>The double click: two identical requests at once. Both answer alike, or the loser is told the first is still running.</summary>
    private async Task<JsonElement> PostTwiceAsync(Buyer buyer, string path, object? body, string sameField)
    {
        var key = Guid.NewGuid().ToString();
        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => SendAsync(buyer, path, body, key)));
        var texts = await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync(Ct)));
        var done = responses.Select((r, i) => (r.StatusCode, Json: texts[i])).Where(x => x.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created).ToArray();
        done.ShouldNotBeEmpty($"{path}: {string.Join(" | ", responses.Select((r, i) => $"{(int)r.StatusCode} {texts[i]}"))}");
        var parsed = done.Select(x => JsonDocument.Parse(x.Json).RootElement.Clone()).ToArray();
        parsed.Select(p => p.GetProperty(sameField).ToString()).Distinct().Count().ShouldBe(1, $"{path}: a double click must give one answer");
        foreach (var (status, text) in responses.Select((r, i) => (r.StatusCode, texts[i])).Where(x => x.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Created)))
        {
            status.ShouldBe(HttpStatusCode.Conflict, text);
            JsonDocument.Parse(text).RootElement.GetProperty("code").GetString().ShouldBe("idempotency.in_progress");
        }

        return parsed[0];
    }

    private Task<HttpResponseMessage> SendAsync(Buyer buyer, string path, object? body, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", buyer.Token);
        request.Headers.Add("Idempotency-Key", key);
        return _http.SendAsync(request, Ct);
    }

    private async Task FinishAtGatewayAsync(Bought bought, Gw outcome)
    {
        var complete = outcome switch
        {
            Gw.Failed => MockOutcome.Failed,
            Gw.Delayed => MockOutcome.SucceededDelayed,
            Gw.Twice => MockOutcome.SucceededTwice,
            Gw.Abandon => (MockOutcome?)null,
            _ => MockOutcome.Succeeded,
        };
        if (complete is { } picked)
        {
            (await _h.Control.CompleteAsync(bought.SessionId, picked, Ct)).IsSuccess.ShouldBeTrue();
        }

        if (outcome == Gw.SucceededThenFailed)
        {
            lock (_failedAfterSuccess)
            {
                _failedAfterSuccess.Add(bought.SessionId);
            }
        }

        if (outcome == Gw.Delayed)
        {
            // The hold ran out while the payment was on its way: somebody else may take the seats before it lands (C3).
            await Store.ReleaseHoldAsync(_h.EventId, bought.HoldId, _gaTiers, Ct);
        }
    }

    // ---------------------------------------------------------------- webhooks, workers

    private MockWebhookDeliverer NewDeliverer() => new(
        Queue, _webhookHttp, _h.Host.Services.GetRequiredService<IOptionsMonitor<MockGatewayOptions>>(),
        _h.Time, NullLogger<MockWebhookDeliverer>.Instance);

    /// <summary>Webhooks that are due, over HTTP to the api's endpoint, by four runners at once (the same body may reach two of them).</summary>
    private async Task DeliverRoundAsync()
    {
        var runners = Enumerable.Range(0, 4).Select(_ => NewDeliverer()).ToArray();
        await Task.WhenAll(runners.Select(r => r.RunOnceAsync(Ct)));
    }

    /// <summary>Rounds until everything due has been accepted, moving the clock by a second between rounds (retry backoff).</summary>
    private async Task DeliverAsync()
    {
        for (var round = 0; round < 8; round++)
        {
            await DeliverRoundAsync();
            _h.Time.Advance(TimeSpan.FromSeconds(1));
        }
    }

    private async Task DeliverWhileAsync(Task running)
    {
        while (!running.IsCompleted)
        {
            await DeliverRoundAsync();
            await Task.Delay(20, Ct);
        }

        await DeliverAsync();
    }

    /// <summary>Time passes: holds and orders end, late payments are found, refunds are paid out, stale sessions are asked again.</summary>
    private async Task SettleAsync()
    {
        _h.Time.Advance(TimeSpan.FromMinutes(10));
        for (var pass = 0; pass < 4; pass++)
        {
            await Task.WhenAll(_h.Sweeper().RunOnceAsync(Ct), _h.Sweeper().RunOnceAsync(Ct));
            await DeliverAsync();
            await Task.WhenAll(_h.Refunds().RunOnceAsync(Ct), _h.Refunds().RunOnceAsync(Ct));
            _h.Time.Advance(TimeSpan.FromMinutes(1));
        }

        _h.Time.Advance(TimeSpan.FromMinutes(11));
        await _h.Sweeper().RunOnceAsync(Ct);
        await _h.Refunds().RunOnceAsync(Ct);
    }

    // ---------------------------------------------------------------- assertions

    private async Task AssertSaleIsConsistentAsync()
    {
        var violations = await CheckAsync();
        violations.ShouldBeEmpty(string.Join(Environment.NewLine, violations.Select(v => v.ToString())));

        var statuses = await _h.WithDbAsync(async db => await db.Orders.AsNoTracking().GroupBy(o => o.Status).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, Ct));
        statuses.Keys.ShouldNotContain(OrderStatus.Pending);
        statuses.Keys.ShouldNotContain(OrderStatus.AwaitingPayment);
        statuses.Keys.ShouldNotContain(OrderStatus.PaymentFailed);
        statuses.Keys.ShouldNotContain(OrderStatus.RefundPending, "every refund the gateway accepts is paid out");
        statuses.GetValueOrDefault(OrderStatus.Paid).ShouldBeGreaterThan(0);

        // The collision really happened and was settled by the database guards, not avoided.
        var lostToResale = await _h.WithDbAsync(db => db.Orders.CountAsync(o => o.Status == OrderStatus.Refunded && o.RefundReason == RefundReasons.SeatsUnavailable, Ct));
        lostToResale.ShouldBeGreaterThan(0, "a late payment for a resold seat must end in a refund");

        var seatsSold = await _h.WithDbAsync(db => db.Tickets.CountAsync(t => t.SeatIndex != null && t.Status != TicketStatus.Voided, Ct));
        seatsSold.ShouldBeLessThanOrEqualTo(HotSeats);
        var gaSold = await _h.WithDbAsync(db => db.Database.SqlQuery<int>($"SELECT sold_count AS \"Value\" FROM ticket_tiers WHERE id = {_h.Ga.Id}").SingleAsync(Ct));
        gaSold.ShouldBeLessThanOrEqualTo(GaCapacity);
    }

    private Task<IReadOnlyList<SaleInvariants.Violation>> CheckAsync() => SaleInvariants.CheckAsync(_h.ConnectionString, Ct);

    private async Task<CheckoutWorkersHarness.Placed> PaidSeatOrderAsync(int seat) => await PaidAsync(_h.Seat(seat));

    private async Task<CheckoutWorkersHarness.Placed> PaidGaOrderAsync(int quantity) => await PaidAsync(_h.GaUnits(quantity));

    private async Task<CheckoutWorkersHarness.Placed> PaidAsync(OrderItem item)
    {
        var placed = await _h.PlaceAsync([item], MockOutcome.Succeeded);
        (await _h.DeliverSucceededAsync(placed)).ShouldBe(PaymentWebhookOutcome.Paid);
        return placed;
    }

    private Task<int> Corrupt(FormattableString sql) => _h.WithDbAsync(db => db.Database.ExecuteSqlAsync(sql, Ct));
}
