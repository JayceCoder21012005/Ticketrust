using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using TicketRush.Api.IntegrationTests.Auth;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Api.IntegrationTests.Tickets;
using TicketRush.Application.Abstractions.Messaging;
using TicketRush.Application.Abstractions.Payments;
using TicketRush.Application.Abstractions.Tickets;
using TicketRush.Application.Features.Orders;
using TicketRush.Application.Features.Resale;
using TicketRush.Application.Features.Waitlist;
using TicketRush.Domain.Events;
using TicketRush.Domain.Orders;
using TicketRush.Domain.Resale;
using TicketRush.Domain.Tickets;
using TicketRush.Domain.Waitlist;
using TicketRush.Infrastructure.Identity;
using TicketRush.Infrastructure.Payments;
using TicketRush.Infrastructure.Persistence.Seed;
using TicketRush.Infrastructure.Redis;

namespace TicketRush.Api.IntegrationTests.Resale;

/// <summary>
/// Spec 14 acceptance over HTTP against Postgres and Redis: resale at ≤ face value through the 08 checkout, transfer by email, the
/// atomic ownership change (old QR refused at the gate, new one admitted), the waitlist's 10-minute window (approach A) and the sweeps.
/// </summary>
public sealed class ResaleApiTests(ContainersFixture containers) : IAsyncLifetime
{
    private const string Slug = TicketTestWorld.SeatedSlug;
    private TicketTestWorld _world = null!;
    private Guid _eventId;
    private bool _gateAssigned;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _world = await TicketTestWorld.CreateAsync(containers);
        _eventId = await _world.EventIdAsync(Slug);
        await _world.SetEventAsync(_eventId, startsAt: DateTimeOffset.UtcNow.AddDays(10), endsAt: DateTimeOffset.UtcNow.AddDays(10).AddHours(3));
    }

    public async ValueTask DisposeAsync() => await _world.DisposeAsync();

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, TicketTestWorld.User? who, object? body = null, bool idempotent = false)
    {
        var request = new HttpRequestMessage(method, path);
        if (who is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", who.Access);
        }

        if (idempotent)
        {
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return _world.Client.SendAsync(request, Ct);
    }

    private static async Task ShouldFailAsync(HttpResponseMessage response, HttpStatusCode status, string code) =>
        (await TicketTestWorld.ReadAsync(response, status)).GetProperty("code").GetString().ShouldBe(code);

    private async Task<(TicketTestWorld.User Seller, IssuedTicket Ticket, long Face)> TicketAsync()
    {
        var seller = await _world.NewUserAsync("Người Bán");
        var ticket = (await _world.IssueSeatedAsync(seller, Slug, 1))[0];
        var face = await _world.WithDbAsync(db => db.Tickets.Where(t => t.Id == ticket.Id)
            .Join(db.Events.SelectMany(e => e.Tiers), t => t.TierId, tier => tier.Id, (t, tier) => tier.Price.Amount).SingleAsync(Ct));
        return (seller, ticket, face);
    }

    private Task<HttpResponseMessage> ListAsync(TicketTestWorld.User seller, Guid ticketId, long price) =>
        SendAsync(HttpMethod.Post, $"/api/v1/tickets/{ticketId}/listings", seller, new { price });

    /// <summary>A listing past the waitlist step: the outbox ran <see cref="ListingCreated"/> (nobody waiting → public at once).</summary>
    private async Task<Guid> PublicListingAsync(TicketTestWorld.User seller, Guid ticketId, long price)
    {
        var listing = await TicketTestWorld.ReadAsync(await ListAsync(seller, ticketId, price));
        await _world.Factory.DrainOutboxAsync(Ct);
        return listing.GetProperty("listingId").GetGuid();
    }

    private Task<HttpResponseMessage> ReserveAsync(TicketTestWorld.User buyer, Guid listingId) =>
        SendAsync(HttpMethod.Post, $"/api/v1/listings/{listingId}/reserve", buyer, idempotent: true);

    /// <summary>Pays the order through 08 (<c>/pay</c> opens the mock session) and delivers the gateway's verified success.</summary>
    private async Task<PaymentWebhookOutcome> PayAsync(TicketTestWorld.User buyer, Guid orderId)
    {
        await TicketTestWorld.ReadAsync(await SendAsync(HttpMethod.Post, $"/api/v1/orders/{orderId}/pay", buyer, idempotent: true));
        return await SucceedAsync(orderId);
    }

    private async Task<PaymentWebhookOutcome> SucceedAsync(Guid orderId)
    {
        var attempt = await _world.WithDbAsync(db => db.PaymentAttempts.AsNoTracking().Where(a => a.OrderId == orderId).OrderBy(a => a.CreatedAt).FirstAsync(Ct));
        await using var scope = _world.Factory.Services.CreateAsyncScope();
        var webhook = new WebhookEvent($"evt_{Guid.NewGuid():N}", attempt.ProviderSessionId, WebhookEventType.Succeeded, attempt.Amount, DateTimeOffset.UtcNow);
        return await scope.ServiceProvider.GetRequiredService<FulfillOrderHandler>().HandleAsync(MockPaymentGateway.ProviderName, webhook, Ct);
    }

    private Task<int> LiveTicketsOnSeatAsync(int seat) =>
        _world.WithDbAsync(db => db.Tickets.CountAsync(t => t.EventId == _eventId && t.SeatIndex == seat && t.Status != TicketStatus.Voided, Ct));

    private Task<ResaleListing> ListingRowAsync(Guid id) => _world.WithDbAsync(db => db.ResaleListings.AsNoTracking().SingleAsync(l => l.Id == id, Ct));

    private Task<Ticket> TicketRowAsync(Guid id) => _world.WithDbAsync(db => db.Tickets.AsNoTracking().SingleAsync(t => t.Id == id, Ct));

    private async Task<int> SweepAsync()
    {
        await using var scope = _world.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ResaleSweepHandler>().HandleAsync(Ct);
    }

    private async Task<string> ScanAsync(Guid ticketId)
    {
        var gate = await LoginAsync("gate@ticketrush.dev");
        if (!_gateAssigned)
        {
            var organizer = await LoginAsync("organizer@ticketrush.dev");
            (await SendAsync(HttpMethod.Post, $"/api/v1/organizer/events/{_eventId}/gate-staff", organizer, new { email = "gate@ticketrush.dev", gateName = "Cổng A" }))
                .StatusCode.ShouldBe(HttpStatusCode.NoContent);
            _gateAssigned = true;
        }

        var qr = _world.Factory.Services.GetRequiredService<IQrCodeService>();
        var code = qr.Encode(ticketId, await _world.SecretOfAsync(ticketId), DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var body = await TicketTestWorld.ReadAsync(await SendAsync(HttpMethod.Post, $"/api/v1/gate/events/{_eventId}/scan", gate, new { payload = code }));
        return body.GetProperty("result").GetString()!;
    }

    private async Task<TicketTestWorld.User> LoginAsync(string email)
    {
        var login = await LoginResult.ReadAsync(await _world.Auth.LoginAsync(email, DevDataSeeder.DevPassword));
        return new TicketTestWorld.User(Guid.Empty, login.AccessToken, email);
    }

    private async Task<string> EmailOfAsync(Guid userId)
    {
        await using var scope = _world.Factory.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>().FindByIdAsync(userId.ToString()))!.Email!;
    }

    [Fact]
    public async Task A_resale_gives_the_buyer_a_new_ticket_and_qr_and_the_gate_refuses_the_sellers_old_one()
    {
        var (seller, ticket, face) = await TicketAsync();
        var buyer = await _world.NewUserAsync("Người Mua");
        var listingId = await PublicListingAsync(seller, ticket.Id, face - 100_000);

        var listed = await TicketTestWorld.ReadAsync(await SendAsync(HttpMethod.Get, $"/api/v1/events/{_eventId}/listings", null));
        listed.EnumerateArray().SelectMany(g => g.GetProperty("listings").EnumerateArray()).Single().GetProperty("price").GetInt64().ShouldBe(face - 100_000);

        var order = await TicketTestWorld.ReadAsync(await ReserveAsync(buyer, listingId));
        var orderId = order.GetProperty("id").GetGuid();
        order.GetProperty("total").GetInt64().ShouldBe(face - 100_000);
        (await PayAsync(buyer, orderId)).ShouldBe(PaymentWebhookOutcome.Paid);

        (await TicketRowAsync(ticket.Id)).Status.ShouldBe(TicketStatus.Voided);
        var mine = await _world.WithDbAsync(db => db.Tickets.AsNoTracking().SingleAsync(t => t.OwnerUserId == buyer.Id, Ct));
        mine.Status.ShouldBe(TicketStatus.Valid);
        mine.SeatIndex.ShouldBe(ticket.SeatIndex);
        mine.OrderId.ShouldBe(orderId);
        (await _world.SecretOfAsync(mine.Id)).ShouldNotBe(await _world.SecretOfAsync(ticket.Id));
        (await ListingRowAsync(listingId)).Status.ShouldBe(ResaleListingStatus.Sold);
        var payout = await _world.WithDbAsync(db => db.SellerPayouts.AsNoTracking().SingleAsync(p => p.ListingId == listingId, Ct));
        (payout.SellerId, payout.Amount.Amount, payout.Status).ShouldBe((seller.Id, face - 100_000, SellerPayoutStatus.Pending));
        (await _world.WithDbAsync(db => db.OutboxMessages.CountAsync(m => m.Type == nameof(TicketReturned), Ct))).ShouldBe(0, "an ownership change gives nothing back to the sale");
        (await LiveTicketsOnSeatAsync(ticket.SeatIndex!.Value)).ShouldBe(1);

        (await ScanAsync(ticket.Id)).ShouldBe("NotValid");
        (await ScanAsync(mine.Id)).ShouldBe("Admitted");
    }

    [Fact]
    public async Task Two_buyers_reserving_the_same_listing_at_once_leave_exactly_one_reservation()
    {
        var (seller, ticket, face) = await TicketAsync();
        var listingId = await PublicListingAsync(seller, ticket.Id, face);
        var first = await _world.NewUserAsync();
        var second = await _world.NewUserAsync();

        var responses = await Task.WhenAll(ReserveAsync(first, listingId), ReserveAsync(second, listingId));

        responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        var loser = responses.Single(r => r.StatusCode != HttpStatusCode.OK);
        await ShouldFailAsync(loser, HttpStatusCode.Conflict, "resale.listing_unavailable");
        (await _world.WithDbAsync(db => db.Orders.CountAsync(o => o.ListingId == listingId, Ct))).ShouldBe(1);
        (await ListingRowAsync(listingId)).Status.ShouldBe(ResaleListingStatus.Reserved);
    }

    [Fact]
    public async Task A_price_above_face_value_is_400_and_a_checked_in_ticket_is_not_allowed()
    {
        var (seller, ticket, face) = await TicketAsync();
        await ShouldFailAsync(await ListAsync(seller, ticket.Id, face + 1), HttpStatusCode.BadRequest, "resale.price_above_cap");

        await _world.SetTicketStatusAsync(ticket.Id, TicketStatus.CheckedIn);
        await ShouldFailAsync(await ListAsync(seller, ticket.Id, face), HttpStatusCode.Conflict, "resale.not_allowed");
        await ShouldFailAsync(
            await SendAsync(HttpMethod.Post, $"/api/v1/tickets/{ticket.Id}/transfers", seller, new { toEmail = "friend@example.com" }),
            HttpStatusCode.Conflict, "transfer.not_allowed");
    }

    [Fact]
    public async Task Another_users_ticket_cannot_be_listed_and_the_seller_cannot_buy_their_own_listing()
    {
        var (seller, ticket, face) = await TicketAsync();
        var stranger = await _world.NewUserAsync();
        await ShouldFailAsync(await ListAsync(stranger, ticket.Id, face), HttpStatusCode.NotFound, "ticket.not_found");

        var listingId = await PublicListingAsync(seller, ticket.Id, face);
        await ShouldFailAsync(await ReserveAsync(seller, listingId), HttpStatusCode.Conflict, "resale.own_listing");
    }

    [Fact]
    public async Task A_transfer_is_emailed_404_for_another_email_and_accepting_twice_is_idempotent()
    {
        var (seller, ticket, _) = await TicketAsync();
        var friend = await _world.NewUserAsync("Bạn Thân");
        var stranger = await _world.NewUserAsync();
        var friendEmail = await EmailOfAsync(friend.Id);

        await ShouldFailAsync(
            await SendAsync(HttpMethod.Post, $"/api/v1/tickets/{ticket.Id}/transfers", seller, new { toEmail = await EmailOfAsync(seller.Id) }),
            HttpStatusCode.BadRequest, "transfer.self");
        var transfer = await TicketTestWorld.ReadAsync(
            await SendAsync(HttpMethod.Post, $"/api/v1/tickets/{ticket.Id}/transfers", seller, new { toEmail = friendEmail.ToUpperInvariant() }));
        var transferId = transfer.GetProperty("transferId").GetGuid();
        (await TicketRowAsync(ticket.Id)).Status.ShouldBe(TicketStatus.Listed);
        await _world.Factory.DrainOutboxAsync(Ct);
        _world.Factory.Emails.Sent.Single(m => m.To == friendEmail).TextBody.ShouldContain($"/transfers/{transferId}");

        await ShouldFailAsync(await SendAsync(HttpMethod.Post, $"/api/v1/transfers/{transferId}/accept", stranger), HttpStatusCode.NotFound, "transfer.not_found");
        await ShouldFailAsync(await SendAsync(HttpMethod.Get, $"/api/v1/transfers/{transferId}", stranger), HttpStatusCode.NotFound, "transfer.not_found");

        var accepted = await TicketTestWorld.ReadAsync(await SendAsync(HttpMethod.Post, $"/api/v1/transfers/{transferId}/accept", friend));
        var again = await TicketTestWorld.ReadAsync(await SendAsync(HttpMethod.Post, $"/api/v1/transfers/{transferId}/accept", friend));

        accepted.GetProperty("status").GetString().ShouldBe("Accepted");
        var newTicket = accepted.GetProperty("newTicketId").GetGuid();
        again.GetProperty("newTicketId").GetGuid().ShouldBe(newTicket);
        (await TicketRowAsync(ticket.Id)).Status.ShouldBe(TicketStatus.Voided);
        var reissued = await TicketRowAsync(newTicket);
        (reissued.OwnerUserId, reissued.Status, reissued.SeatIndex, reissued.OrderId).ShouldBe((friend.Id, TicketStatus.Valid, ticket.SeatIndex, ticket.OrderId));
        (await LiveTicketsOnSeatAsync(ticket.SeatIndex!.Value)).ShouldBe(1);
        (await ScanAsync(ticket.Id)).ShouldBe("NotValid");
    }

    [Fact]
    public async Task Cancelling_a_transfer_or_a_listing_gives_the_ticket_back()
    {
        var (seller, ticket, face) = await TicketAsync();
        var transfer = await TicketTestWorld.ReadAsync(
            await SendAsync(HttpMethod.Post, $"/api/v1/tickets/{ticket.Id}/transfers", seller, new { toEmail = "friend@example.com" }));
        await ShouldFailAsync(await ListAsync(seller, ticket.Id, face), HttpStatusCode.Conflict, "resale.not_allowed");

        (await SendAsync(HttpMethod.Delete, $"/api/v1/transfers/{transfer.GetProperty("transferId").GetGuid()}", seller)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await TicketRowAsync(ticket.Id)).Status.ShouldBe(TicketStatus.Valid);

        var listingId = await PublicListingAsync(seller, ticket.Id, face);
        (await SendAsync(HttpMethod.Delete, $"/api/v1/listings/{listingId}", seller)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await TicketRowAsync(ticket.Id)).Status.ShouldBe(TicketStatus.Valid);
        (await ListingRowAsync(listingId)).Status.ShouldBe(ResaleListingStatus.Cancelled);
    }

    [Fact]
    public async Task A_listing_and_a_transfer_of_one_ticket_at_once_leave_one_of_them_and_one_live_ticket_per_seat()
    {
        var (seller, ticket, face) = await TicketAsync();
        var friend = await _world.NewUserAsync();

        var responses = await Task.WhenAll(
            ListAsync(seller, ticket.Id, face),
            SendAsync(HttpMethod.Post, $"/api/v1/tickets/{ticket.Id}/transfers", seller, new { toEmail = await EmailOfAsync(friend.Id) }));

        responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        var listings = await _world.WithDbAsync(db => db.ResaleListings.CountAsync(l => l.TicketId == ticket.Id, Ct));
        var transfers = await _world.WithDbAsync(db => db.TicketTransfers.CountAsync(t => t.TicketId == ticket.Id, Ct));
        (listings + transfers).ShouldBe(1);

        // Finish whichever won, then check the invariant on every seat of the event.
        if (transfers == 1)
        {
            var transferId = await _world.WithDbAsync(db => db.TicketTransfers.Where(t => t.TicketId == ticket.Id).Select(t => t.Id).SingleAsync(Ct));
            await TicketTestWorld.ReadAsync(await SendAsync(HttpMethod.Post, $"/api/v1/transfers/{transferId}/accept", friend));
        }
        else
        {
            await _world.Factory.DrainOutboxAsync(Ct);
            var listingId = await _world.WithDbAsync(db => db.ResaleListings.Where(l => l.TicketId == ticket.Id).Select(l => l.Id).SingleAsync(Ct));
            var order = await TicketTestWorld.ReadAsync(await ReserveAsync(friend, listingId));
            (await PayAsync(friend, order.GetProperty("id").GetGuid())).ShouldBe(PaymentWebhookOutcome.Paid);
        }

        var doubled = await _world.WithDbAsync(db => db.Tickets.Where(t => t.EventId == _eventId && t.SeatIndex != null && t.Status != TicketStatus.Voided)
            .GroupBy(t => t.SeatIndex).CountAsync(g => g.Count() > 1, Ct));
        doubled.ShouldBe(0);
        (await _world.WithDbAsync(db => db.Tickets.CountAsync(t => t.OwnerUserId == friend.Id && t.Status == TicketStatus.Valid, Ct))).ShouldBe(1);
    }

    [Fact]
    public async Task A_new_listing_is_the_oldest_matching_waiters_for_ten_minutes_and_public_after_their_window()
    {
        var (seller, ticket, face) = await TicketAsync();
        var wantsTwo = await _world.NewUserAsync();
        var waiter = await _world.NewUserAsync();
        var later = await _world.NewUserAsync();
        var outsider = await _world.NewUserAsync();
        var start = DateTimeOffset.UtcNow.AddMinutes(-5);
        await _world.WithDbAsync(db =>
        {
            db.WaitlistEntries.Add(WaitlistEntry.Join(_eventId, wantsTwo.Id, null, 2, start));
            db.WaitlistEntries.Add(WaitlistEntry.Join(_eventId, waiter.Id, null, 1, start.AddSeconds(1)));
            db.WaitlistEntries.Add(WaitlistEntry.Join(_eventId, later.Id, null, 1, start.AddSeconds(2)));
            return db.SaveChangesAsync(Ct);
        });

        var listingId = await PublicListingAsync(seller, ticket.Id, face);

        var offered = await _world.WithDbAsync(db => db.WaitlistEntries.AsNoTracking().SingleAsync(e => e.UserId == waiter.Id, Ct));
        offered.Status.ShouldBe(WaitlistStatus.Offered);
        offered.OfferListingId.ShouldBe(listingId);
        (await ListingRowAsync(listingId)).OfferedToUserId.ShouldBe(waiter.Id);
        (await TicketTestWorld.ReadAsync(await SendAsync(HttpMethod.Get, $"/api/v1/events/{_eventId}/listings", null))).GetArrayLength().ShouldBe(0);
        await ShouldFailAsync(await ReserveAsync(outsider, listingId), HttpStatusCode.Conflict, "resale.listing_unavailable");
        var offer = await TicketTestWorld.ReadAsync(await SendAsync(HttpMethod.Get, $"/api/v1/waitlist/offers/{offered.Id}", waiter));
        offer.GetProperty("listing").GetProperty("price").GetInt64().ShouldBe(face);
        offer.GetProperty("hold").ValueKind.ShouldBe(JsonValueKind.Null);
        await _world.Factory.DrainOutboxAsync(Ct);
        (await _world.WithDbAsync(db => db.Notifications.AsNoTracking().SingleAsync(n => n.UserId == waiter.Id, Ct)))
            .Body.ShouldContain("mở bán cho mọi người");

        // The window ends unused: the waiter misses once and the public sees the listing.
        await _world.WithDbAsync(async db =>
        {
            var past = DateTimeOffset.UtcNow.AddSeconds(-1);
            await db.Database.ExecuteSqlAsync($"UPDATE resale_listings SET waitlist_until = {past} WHERE id = {listingId}", Ct);
            return await db.Database.ExecuteSqlAsync($"UPDATE waitlist_entries SET offer_expires_at = {past} WHERE id = {offered.Id}", Ct);
        });
        await using (var scope = _world.Factory.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<WaitlistOffers>().ExpireDueAsync(Ct)).ShouldBe(1);
        }

        var missed = await _world.WithDbAsync(db => db.WaitlistEntries.AsNoTracking().SingleAsync(e => e.UserId == waiter.Id, Ct));
        (missed.Status, missed.Misses).ShouldBe((WaitlistStatus.Waiting, 1));
        (await _world.WithDbAsync(db => db.WaitlistEntries.AsNoTracking().SingleAsync(e => e.UserId == later.Id, Ct))).Status.ShouldBe(WaitlistStatus.Waiting, "nothing passes on");
        (await TicketTestWorld.ReadAsync(await SendAsync(HttpMethod.Get, $"/api/v1/events/{_eventId}/listings", null))).GetArrayLength().ShouldBe(1);
        (await ReserveAsync(outsider, listingId)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_offered_waiter_buys_through_the_offer_and_their_entry_converts()
    {
        var (seller, ticket, face) = await TicketAsync();
        var waiter = await _world.NewUserAsync();
        var entry = WaitlistEntry.Join(_eventId, waiter.Id, null, 1, DateTimeOffset.UtcNow.AddMinutes(-1));
        await _world.WithDbAsync(db =>
        {
            db.WaitlistEntries.Add(entry);
            return db.SaveChangesAsync(Ct);
        });
        await PublicListingAsync(seller, ticket.Id, face);

        var accepted = await TicketTestWorld.ReadAsync(await SendAsync(HttpMethod.Post, $"/api/v1/waitlist/offers/{entry.Id}/accept", waiter));
        var orderId = accepted.GetProperty("orderId").GetGuid();
        (await PayAsync(waiter, orderId)).ShouldBe(PaymentWebhookOutcome.Paid);
        await _world.Factory.DrainOutboxAsync(Ct);

        (await _world.WithDbAsync(db => db.WaitlistEntries.AsNoTracking().SingleAsync(e => e.Id == entry.Id, Ct))).Status.ShouldBe(WaitlistStatus.Converted);
        (await _world.WithDbAsync(db => db.Tickets.CountAsync(t => t.OwnerUserId == waiter.Id && t.Status == TicketStatus.Valid, Ct))).ShouldBe(1);
    }

    [Fact]
    public async Task An_expired_reservation_puts_the_listing_back_and_a_late_payment_after_another_buyer_reserved_is_refunded()
    {
        var (seller, ticket, face) = await TicketAsync();
        var listingId = await PublicListingAsync(seller, ticket.Id, face);
        var late = await _world.NewUserAsync();
        var next = await _world.NewUserAsync();
        var lateOrder = (await TicketTestWorld.ReadAsync(await ReserveAsync(late, listingId))).GetProperty("id").GetGuid();
        await TicketTestWorld.ReadAsync(await SendAsync(HttpMethod.Post, $"/api/v1/orders/{lateOrder}/pay", late, idempotent: true));

        await _world.WithDbAsync(db => db.Database.ExecuteSqlAsync($"UPDATE orders SET expires_at = {DateTimeOffset.UtcNow.AddSeconds(-1)} WHERE id = {lateOrder}", Ct));
        await using (var scope = _world.Factory.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<ExpireOrderHandler>().HandleAsync(lateOrder, new HashSet<string>(), Ct)).ShouldBe(OrderExpiryOutcome.Expired);
        }

        (await ListingRowAsync(listingId)).Status.ShouldBe(ResaleListingStatus.Active);
        var redis = _world.Factory.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase();
        var key = $"{_world.Factory.Services.GetRequiredService<RedisKeys>().Prefix}resale:{{{_eventId:N}}}:listing:{listingId:N}";
        (await redis.KeyExistsAsync(key)).ShouldBeFalse();

        (await ReserveAsync(next, listingId)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SucceedAsync(lateOrder)).ShouldBe(PaymentWebhookOutcome.RefundPending);
        (await _world.WithDbAsync(db => db.Orders.AsNoTracking().SingleAsync(o => o.Id == lateOrder, Ct))).RefundReason.ShouldBe(RefundReasons.SeatsUnavailable);
        (await TicketRowAsync(ticket.Id)).Status.ShouldBe(TicketStatus.Listed, "the seller's ticket waits for the next buyer");
        (await LiveTicketsOnSeatAsync(ticket.SeatIndex!.Value)).ShouldBe(1);
    }

    [Fact]
    public async Task An_unconfirmed_buyer_cannot_reserve()
    {
        var (seller, ticket, face) = await TicketAsync();
        var listingId = await PublicListingAsync(seller, ticket.Id, face);
        var email = $"unconfirmed-{Guid.NewGuid():N}@example.com";
        await using (var scope = _world.Factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = new AppUser { Id = Guid.CreateVersion7(), Email = email, UserName = email, FullName = "Chưa Xác Nhận", EmailConfirmed = false };
            (await users.CreateAsync(user, AuthApi.Password)).Succeeded.ShouldBeTrue();
            (await users.AddToRoleAsync(user, "Customer")).Succeeded.ShouldBeTrue();
        }

        var login = await LoginResult.ReadAsync(await _world.Auth.LoginAsync(email));
        await ShouldFailAsync(await ReserveAsync(new TicketTestWorld.User(Guid.Empty, login.AccessToken, email), listingId), HttpStatusCode.Forbidden, "resale.email_not_confirmed");
    }

    [Fact]
    public async Task The_sweep_expires_old_transfers_closes_resale_at_the_cutoff_and_pays_out_after_the_event()
    {
        var (seller, ticket, face) = await TicketAsync();
        var (other, otherTicket, otherFace) = await TicketAsync();
        var buyer = await _world.NewUserAsync();
        var transfer = await TicketTestWorld.ReadAsync(
            await SendAsync(HttpMethod.Post, $"/api/v1/tickets/{ticket.Id}/transfers", seller, new { toEmail = "friend@example.com" }));
        var transferId = transfer.GetProperty("transferId").GetGuid();
        await _world.WithDbAsync(db => db.Database.ExecuteSqlAsync($"UPDATE ticket_transfers SET expires_at = {DateTimeOffset.UtcNow.AddSeconds(-1)} WHERE id = {transferId}", Ct));
        await SweepAsync();
        (await _world.WithDbAsync(db => db.TicketTransfers.AsNoTracking().SingleAsync(t => t.Id == transferId, Ct))).Status.ShouldBe(TicketTransferStatus.Expired);
        (await TicketRowAsync(ticket.Id)).Status.ShouldBe(TicketStatus.Valid);

        var sold = await PublicListingAsync(other, otherTicket.Id, otherFace);
        var order = await TicketTestWorld.ReadAsync(await ReserveAsync(buyer, sold));
        (await PayAsync(buyer, order.GetProperty("id").GetGuid())).ShouldBe(PaymentWebhookOutcome.Paid);
        var unsold = await PublicListingAsync(seller, ticket.Id, face);

        // Inside the 2-hour cutoff: no new listing, and the open one ends with the ticket usable again.
        await _world.SetEventAsync(_eventId, startsAt: DateTimeOffset.UtcNow.AddHours(1), endsAt: DateTimeOffset.UtcNow.AddHours(4));
        await SweepAsync();
        (await ListingRowAsync(unsold)).Status.ShouldBe(ResaleListingStatus.Cancelled);
        (await TicketRowAsync(ticket.Id)).Status.ShouldBe(TicketStatus.Valid);
        await ShouldFailAsync(await ListAsync(seller, ticket.Id, face), HttpStatusCode.Conflict, "resale.not_allowed");
        (await _world.WithDbAsync(db => db.SellerPayouts.AsNoTracking().SingleAsync(p => p.ListingId == sold, Ct))).Status.ShouldBe(SellerPayoutStatus.Pending);

        await _world.SetEventAsync(_eventId, startsAt: DateTimeOffset.UtcNow.AddHours(-4), endsAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        await SweepAsync();
        var paid = await _world.WithDbAsync(db => db.SellerPayouts.AsNoTracking().SingleAsync(p => p.ListingId == sold, Ct));
        paid.Status.ShouldBe(SellerPayoutStatus.Paid);
        paid.PaidAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_cancelled_event_ends_its_listings_and_transfers_and_cancels_pending_payouts()
    {
        var (seller, ticket, face) = await TicketAsync();
        var (other, otherTicket, otherFace) = await TicketAsync();
        var (third, thirdTicket, _) = await TicketAsync();
        var buyer = await _world.NewUserAsync();
        var sold = await PublicListingAsync(other, otherTicket.Id, otherFace);
        var order = await TicketTestWorld.ReadAsync(await ReserveAsync(buyer, sold));
        (await PayAsync(buyer, order.GetProperty("id").GetGuid())).ShouldBe(PaymentWebhookOutcome.Paid);
        var open = await PublicListingAsync(seller, ticket.Id, face);
        var transfer = await TicketTestWorld.ReadAsync(
            await SendAsync(HttpMethod.Post, $"/api/v1/tickets/{thirdTicket.Id}/transfers", third, new { toEmail = "friend@example.com" }));

        await using (var scope = _world.Factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<EventCancelledResaleHandler>()
                .HandleAsync(new EventCancelled(_eventId, Guid.Empty), new IntegrationEventContext(Guid.NewGuid(), DateTimeOffset.UtcNow, 1), Ct);
            await scope.ServiceProvider.GetRequiredService<TicketRush.Infrastructure.Persistence.AppDbContext>().SaveChangesAsync(Ct);
        }

        (await ListingRowAsync(open)).Status.ShouldBe(ResaleListingStatus.Cancelled);
        (await _world.WithDbAsync(db => db.TicketTransfers.AsNoTracking().SingleAsync(t => t.Id == transfer.GetProperty("transferId").GetGuid(), Ct)))
            .Status.ShouldBe(TicketTransferStatus.Cancelled);
        (await _world.WithDbAsync(db => db.SellerPayouts.AsNoTracking().SingleAsync(p => p.ListingId == sold, Ct))).Status.ShouldBe(SellerPayoutStatus.Cancelled);
    }

    [Fact]
    public async Task The_ticket_view_shows_the_face_value_and_the_live_listing()
    {
        var (seller, ticket, face) = await TicketAsync();
        var before = await _world.JsonAsync($"/api/v1/tickets/{ticket.Id}", seller);
        before.GetProperty("resale").GetProperty("faceValue").GetInt64().ShouldBe(face);
        before.GetProperty("resale").GetProperty("canChange").GetBoolean().ShouldBeTrue();

        var listingId = await PublicListingAsync(seller, ticket.Id, face - 1);

        var after = await _world.JsonAsync($"/api/v1/tickets/{ticket.Id}", seller);
        after.GetProperty("status").GetString().ShouldBe("Listed");
        after.GetProperty("resale").GetProperty("canChange").GetBoolean().ShouldBeFalse();
        after.GetProperty("resale").GetProperty("listing").GetProperty("listingId").GetGuid().ShouldBe(listingId);
    }
}
