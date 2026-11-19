using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Api.IntegrationTests.Orders;
using TicketRush.Application.Abstractions.Inventory;
using TicketRush.Application.Abstractions.Messaging;
using TicketRush.Application.Features.Waitlist;
using TicketRush.Application.Features.Notifications;
using TicketRush.Domain.Orders;
using TicketRush.Domain.Tickets;
using TicketRush.Domain.Waitlist;
using TicketRush.Infrastructure.Redis;
using Order = TicketRush.Domain.Orders.Order;

namespace TicketRush.Api.IntegrationTests.Waitlist;

/// <summary>
/// Spec 13 acceptance against Postgres and Redis: a returned ticket goes to the oldest matching waiter as a 15-minute hold, an
/// unused offer passes to the next waiter without the seat ever being free or held for two users, and paying converts the entry.
/// </summary>
public sealed class WaitlistOfferTests(ContainersFixture containers) : IAsyncLifetime
{
    private static readonly Guid PreviousOwner = Guid.NewGuid();
    private CheckoutWorkersHarness _h = null!;
    private int _seat;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _h = await CheckoutWorkersHarness.CreateAsync(containers);
        _seat = _h.Seats[3];
        await using var scope = _h.Host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IInventorySeeder>().SeedAsync(_h.EventId, Ct);
        await Redis.SetAddAsync(Keys.InvSold(_h.EventId), _seat);
        await Redis.StringSetAsync(Keys.InvBought(_h.EventId, PreviousOwner), 1);
    }

    public async ValueTask DisposeAsync() => await _h.DisposeAsync();

    private IDatabase Redis => _h.Host.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase();

    private RedisKeys Keys => _h.Host.Services.GetRequiredService<RedisKeys>();

    private async Task<Guid> JoinAsync(int quantity = 1, Guid? tier = null)
    {
        var entry = WaitlistEntry.Join(_h.EventId, Guid.NewGuid(), tier, quantity, _h.Time.GetUtcNow());
        await _h.WithDbAsync(db =>
        {
            db.WaitlistEntries.Add(entry);
            return db.SaveChangesAsync(Ct);
        });
        _h.Time.Advance(TimeSpan.FromSeconds(1));
        return entry.Id;
    }

    private Task<WaitlistEntry> EntryAsync(Guid id) => _h.WithDbAsync(db => db.WaitlistEntries.AsNoTracking().SingleAsync(e => e.Id == id, Ct));

    private TicketReturned Returned(Guid? ticketId = null) => new(ticketId ?? Guid.NewGuid(), _h.EventId, _h.Seated.Id, _seat, PreviousOwner);

    /// <summary>One delivery of the message to the waitlist consumer, committed like the outbox does.</summary>
    private async Task DeliverAsync(TicketReturned returned)
    {
        await using var scope = _h.Host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TicketReturnedWaitlistHandler>()
            .HandleAsync(returned, new IntegrationEventContext(Guid.NewGuid(), _h.Time.GetUtcNow(), 1), Ct);
        await scope.ServiceProvider.GetRequiredService<TicketRush.Infrastructure.Persistence.AppDbContext>().SaveChangesAsync(Ct);
    }

    private async Task<int> ExpireAsync()
    {
        await using var scope = _h.Host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<WaitlistOffers>().ExpireDueAsync(Ct);
    }

    private async Task<string?> SeatOwnerAsync() => await Redis.HashGetAsync(Keys.InvHeld(_h.EventId), _seat);

    [Fact]
    public async Task Three_waiting_users_and_one_returned_ticket_offer_it_to_the_oldest_matching_user_only()
    {
        var wantsTwo = await JoinAsync(quantity: 2);
        var oldest = await JoinAsync();
        var next = await JoinAsync();

        await DeliverAsync(Returned());

        var offered = await EntryAsync(oldest);
        offered.Status.ShouldBe(WaitlistStatus.Offered);
        offered.OfferExpiresAt.ShouldNotBeNull().ShouldBe(_h.Time.GetUtcNow() + TimeSpan.FromMinutes(15), TimeSpan.FromMilliseconds(1));
        (await SeatOwnerAsync()).ShouldBe(offered.OfferHoldId!.Value.ToString("N"));
        (await EntryAsync(wantsTwo)).Status.ShouldBe(WaitlistStatus.Waiting);
        (await EntryAsync(next)).Status.ShouldBe(WaitlistStatus.Waiting);
        (await _h.OutboxCountAsync(nameof(WaitlistOfferIssued))).ShouldBe(1);
    }

    [Fact]
    public async Task With_nobody_waiting_the_returned_seat_goes_back_on_sale()
    {
        await DeliverAsync(Returned());

        (await Redis.SetContainsAsync(Keys.InvSold(_h.EventId), _seat)).ShouldBeFalse();
        (await SeatOwnerAsync()).ShouldBeNull();
        ((int)await Redis.StringGetAsync(Keys.InvBought(_h.EventId, PreviousOwner))).ShouldBe(0);
    }

    [Fact]
    public async Task An_expired_offer_passes_to_the_next_user_and_the_first_goes_to_the_back_once()
    {
        var first = await JoinAsync();
        var second = await JoinAsync();
        await DeliverAsync(Returned());
        (await ExpireAsync()).ShouldBe(0, "nothing is due before the offer runs out");

        _h.Time.Advance(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(1));
        (await ExpireAsync()).ShouldBe(1);

        var passed = await EntryAsync(second);
        passed.Status.ShouldBe(WaitlistStatus.Offered);
        (await SeatOwnerAsync()).ShouldBe(passed.OfferHoldId!.Value.ToString("N"), "the seat moved straight to the next hold");
        var missed = await EntryAsync(first);
        missed.Status.ShouldBe(WaitlistStatus.Waiting);
        missed.Misses.ShouldBe(1);
        (await Redis.StringGetAsync(Keys.InvUserHold(_h.EventId, missed.UserId))).IsNull.ShouldBeTrue();
    }

    [Fact]
    public async Task An_expired_offer_with_nobody_else_waiting_frees_the_seat()
    {
        var only = await JoinAsync();
        await DeliverAsync(Returned());

        _h.Time.Advance(TimeSpan.FromMinutes(16));
        await ExpireAsync();

        (await SeatOwnerAsync()).ShouldBeNull();
        (await Redis.SetContainsAsync(Keys.InvSold(_h.EventId), _seat)).ShouldBeFalse();
        (await EntryAsync(only)).Status.ShouldBe(WaitlistStatus.Waiting, "back of the (empty) line after its first miss");
    }

    [Fact]
    public async Task A_redelivered_return_never_offers_the_same_seat_twice()
    {
        var first = await JoinAsync();
        var second = await JoinAsync();
        var returned = Returned();

        await DeliverAsync(returned);
        await DeliverAsync(returned);

        (await EntryAsync(first)).Status.ShouldBe(WaitlistStatus.Offered);
        (await EntryAsync(second)).Status.ShouldBe(WaitlistStatus.Waiting);
    }

    [Fact]
    public async Task Paying_for_the_offered_hold_converts_the_entry()
    {
        var entryId = await JoinAsync();
        await DeliverAsync(Returned());
        var entry = await EntryAsync(entryId);

        await using (var scope = _h.Host.Services.CreateAsyncScope())
        {
            var paid = new OrderPaid(Guid.NewGuid(), entry.UserId, _h.EventId, entry.OfferHoldId!.Value, 1, "VND", 1, _h.Time.GetUtcNow(), []);
            await scope.ServiceProvider.GetRequiredService<OrderPaidWaitlistHandler>()
                .HandleAsync(paid, new IntegrationEventContext(Guid.NewGuid(), _h.Time.GetUtcNow(), 1), Ct);
            await scope.ServiceProvider.GetRequiredService<TicketRush.Infrastructure.Persistence.AppDbContext>().SaveChangesAsync(Ct);
        }

        (await EntryAsync(entryId)).Status.ShouldBe(WaitlistStatus.Converted);
    }

    [Fact]
    public async Task Releasing_the_offered_hold_declines_the_offer_and_passes_the_seat_on()
    {
        var first = await JoinAsync();
        var second = await JoinAsync();
        await DeliverAsync(Returned());
        var offered = await EntryAsync(first);

        await using (var scope = _h.Host.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<TicketRush.Application.Features.Holds.ReleaseMyHoldHandler>()
                .HandleAsync(offered.UserId, _h.EventId, Ct)).IsSuccess.ShouldBeTrue();
        }

        (await EntryAsync(first)).Status.ShouldBe(WaitlistStatus.Left);
        var next = await EntryAsync(second);
        next.Status.ShouldBe(WaitlistStatus.Offered);
        (await SeatOwnerAsync()).ShouldBe(next.OfferHoldId!.Value.ToString("N"));
    }

    [Fact]
    public async Task Leaving_with_a_live_offer_passes_the_seat_to_the_next_in_line()
    {
        var first = await JoinAsync();
        var second = await JoinAsync();
        await DeliverAsync(Returned());
        var offered = await EntryAsync(first);

        await using (var scope = _h.Host.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<LeaveWaitlistHandler>().HandleAsync(offered.UserId, _h.EventId, Ct)).IsSuccess.ShouldBeTrue();
        }

        (await EntryAsync(first)).Status.ShouldBe(WaitlistStatus.Left);
        (await EntryAsync(second)).Status.ShouldBe(WaitlistStatus.Offered);
    }

    [Fact]
    public async Task An_offer_whose_order_is_still_being_paid_is_not_passed_on()
    {
        var first = await JoinAsync();
        await JoinAsync();
        await DeliverAsync(Returned());
        var offered = await EntryAsync(first);
        await _h.WithDbAsync(db =>
        {
            db.Orders.Add(Order.Create(offered.UserId, _h.EventId, offered.OfferHoldId!.Value, [_h.Seat(_seat)], new TicketRush.Domain.Primitives.Money(_h.Seated.Price),
                new BuyerInfo("An", "an@example.com", null), offered.OfferExpiresAt!.Value, _h.Time.GetUtcNow()));
            return db.SaveChangesAsync(Ct);
        });

        _h.Time.Advance(TimeSpan.FromMinutes(16));

        (await ExpireAsync()).ShouldBe(0);
        (await EntryAsync(first)).Status.ShouldBe(WaitlistStatus.Offered);
    }

    [Fact]
    public async Task An_offer_is_sent_in_app_and_by_email_once_with_the_link_to_the_offer_page()
    {
        Guid userId;
        await using (var scope = _h.Host.Services.CreateAsyncScope())
        {
            userId = Guid.CreateVersion7();
            var email = $"wait-{userId:N}@example.com";
            var created = await scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<TicketRush.Infrastructure.Identity.AppUser>>()
                .CreateAsync(new TicketRush.Infrastructure.Identity.AppUser { Id = userId, UserName = email, Email = email, FullName = "Nguyễn Văn Đạt", EmailConfirmed = true });
            created.Succeeded.ShouldBeTrue();
        }

        var entry = WaitlistEntry.Join(_h.EventId, userId, null, 1, _h.Time.GetUtcNow());
        await _h.WithDbAsync(db =>
        {
            db.WaitlistEntries.Add(entry);
            return db.SaveChangesAsync(Ct);
        });
        var issued = new WaitlistOfferIssued(entry.Id, _h.EventId, userId, Guid.NewGuid(), _h.Time.GetUtcNow().AddMinutes(15));

        for (var delivery = 0; delivery < 2; delivery++)
        {
            await using var scope = _h.Host.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<WaitlistOfferNotificationHandler>()
                .HandleAsync(issued, new IntegrationEventContext(Guid.NewGuid(), _h.Time.GetUtcNow(), 1), Ct);
            await scope.ServiceProvider.GetRequiredService<TicketRush.Infrastructure.Persistence.AppDbContext>().SaveChangesAsync(Ct);
        }

        var notice = await _h.WithDbAsync(db => db.Notifications.AsNoTracking().Where(n => n.UserId == userId).ToListAsync(Ct));
        notice.ShouldHaveSingleItem().Link.ShouldBe($"/waitlist/offers/{entry.Id}");
        notice[0].Title.ShouldBe("Có vé cho bạn!");
        var emails = await _h.WithDbAsync(db => db.EmailDeliveries.AsNoTracking().Where(e => e.UserId == userId).ToListAsync(Ct));
        emails.ShouldHaveSingleItem().Template.ShouldBe("waitlist-offer");
    }
}
