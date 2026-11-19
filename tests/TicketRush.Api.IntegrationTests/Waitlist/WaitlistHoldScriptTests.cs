using System.Text.Json;
using StackExchange.Redis;
using TicketRush.Api.IntegrationTests.Holds;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Application.Abstractions.Inventory;

namespace TicketRush.Api.IntegrationTests.Waitlist;

/// <summary>waitlist_hold.lua against a real Redis (spec 13): a returned ticket is never free while someone waits for it.</summary>
public sealed class WaitlistHoldScriptTests(ContainersFixture containers) : IAsyncLifetime
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);
    private static readonly Guid Buyer = Guid.NewGuid();
    private static readonly Guid Waiter = Guid.NewGuid();
    private static readonly Guid NextWaiter = Guid.NewGuid();
    private InventoryHarness _h = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _h = await InventoryHarness.CreateAsync(containers);

    public async ValueTask DisposeAsync() => await _h.DisposeAsync();

    private async Task SellAsync(params int[] seats)
    {
        foreach (var seat in seats) await _h.Db.SetAddAsync(_h.Keys.InvSold(_h.EventId), seat);
        await _h.Db.StringSetAsync(_h.Keys.InvBought(_h.EventId, Buyer), seats.Length);
    }

    [Fact]
    public async Task A_returned_seat_goes_straight_from_sold_to_the_waiters_hold()
    {
        await SellAsync(5, 6);
        var messages = await _h.ListenAsync();
        var hold = Guid.NewGuid();

        var outcome = await _h.Store.HoldReturnedAsync(_h.EventId, hold, Waiter, [5], [], Buyer, Ttl, Ct);

        outcome.Status.ShouldBe(WaitlistHoldStatus.Ok);
        outcome.ExpiresAt.ShouldNotBeNull().ShouldBe(_h.Time.GetUtcNow() + Ttl, TimeSpan.FromMilliseconds(1));
        (await _h.Db.SetContainsAsync(_h.Keys.InvSold(_h.EventId), 5)).ShouldBeFalse();
        (await _h.OwnerOfAsync(5)).ShouldBe(hold.ToString("N"));
        ((int)await _h.Db.StringGetAsync(_h.Keys.InvBought(_h.EventId, Buyer))).ShouldBe(1);
        ((string?)await _h.Db.StringGetAsync(_h.Keys.InvUserHold(_h.EventId, Waiter))).ShouldBe(hold.ToString("N"));
        ((string?)await _h.Db.HashGetAsync(_h.Keys.InvHold(_h.EventId, hold), "waitlist")).ShouldBe("1");
        (await _h.Db.SortedSetScoreAsync(_h.Keys.InvExpiry(_h.EventId), hold.ToString("N"))).ShouldBeNull("the sweeper must never free a waitlist hold");
        (await _h.Store.GetUserHoldAsync(_h.EventId, Waiter, Ct)).ShouldNotBeNull().Seats.ShouldBe([5]);

        await WaitUntilAsync(() => !messages.IsEmpty);
        using var delta = JsonDocument.Parse(messages.Single());
        delta.RootElement.GetProperty("held").EnumerateArray().Select(e => e.GetInt32()).ShouldBe([5]);
        delta.RootElement.GetProperty("freed").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task A_returned_ga_ticket_moves_from_sold_to_held_without_touching_what_is_left()
    {
        var before = (int)await _h.Db.StringGetAsync(_h.Keys.InvGa(_h.EventId, _h.GaTier));

        var outcome = await _h.Store.HoldReturnedAsync(_h.EventId, Guid.NewGuid(), Waiter, [], [new GaLine(_h.GaTier, 1)], Buyer, Ttl, Ct);

        outcome.Status.ShouldBe(WaitlistHoldStatus.Ok);
        ((int)await _h.Db.StringGetAsync(_h.Keys.InvGa(_h.EventId, _h.GaTier))).ShouldBe(before);
        ((int)await _h.Db.HashGetAsync(_h.Keys.InvGaHeld(_h.EventId), _h.GaTier.ToString("N"))).ShouldBe(1);
    }

    [Fact]
    public async Task A_seat_someone_else_holds_is_refused_and_nothing_changes()
    {
        await _h.HoldAsync(Guid.NewGuid(), 5);

        var outcome = await _h.Store.HoldReturnedAsync(_h.EventId, Guid.NewGuid(), Waiter, [5], [], Buyer, Ttl, Ct);

        outcome.Status.ShouldBe(WaitlistHoldStatus.Taken);
        (await _h.Db.KeyExistsAsync(_h.Keys.InvUserHold(_h.EventId, Waiter))).ShouldBeFalse();
    }

    [Fact]
    public async Task A_waiter_who_already_holds_tickets_of_the_event_is_skipped()
    {
        await SellAsync(5);
        await _h.HoldAsync(Waiter, 9);

        var outcome = await _h.Store.HoldReturnedAsync(_h.EventId, Guid.NewGuid(), Waiter, [5], [], Buyer, Ttl, Ct);

        outcome.Status.ShouldBe(WaitlistHoldStatus.HasHold);
        (await _h.Db.SetContainsAsync(_h.Keys.InvSold(_h.EventId), 5)).ShouldBeTrue();
    }

    [Fact]
    public async Task Passing_an_expired_offer_moves_the_seat_to_the_next_waiter_without_ever_freeing_it()
    {
        await SellAsync(5);
        var first = Guid.NewGuid();
        await _h.Store.HoldReturnedAsync(_h.EventId, first, Waiter, [5], [], Buyer, Ttl, Ct);
        var messages = await _h.ListenAsync();
        var second = Guid.NewGuid();

        var outcome = await _h.Store.PassHoldAsync(_h.EventId, first, Waiter, second, NextWaiter, Ttl, Ct);

        outcome.Status.ShouldBe(WaitlistHoldStatus.Ok);
        (await _h.OwnerOfAsync(5)).ShouldBe(second.ToString("N"));
        (await _h.Db.KeyExistsAsync(_h.Keys.InvHold(_h.EventId, first))).ShouldBeFalse();
        (await _h.Db.KeyExistsAsync(_h.Keys.InvUserHold(_h.EventId, Waiter))).ShouldBeFalse();
        ((string?)await _h.Db.StringGetAsync(_h.Keys.InvUserHold(_h.EventId, NextWaiter))).ShouldBe(second.ToString("N"));
        await WaitUntilAsync(() => !messages.IsEmpty);
        using var delta = JsonDocument.Parse(messages.Single());
        delta.RootElement.GetProperty("freed").GetArrayLength().ShouldBe(0);

        (await _h.Store.PassHoldAsync(_h.EventId, first, Waiter, Guid.NewGuid(), Guid.NewGuid(), Ttl, Ct)).Status.ShouldBe(WaitlistHoldStatus.NotFound);
    }

    [Fact]
    public async Task With_nobody_waiting_a_returned_ticket_goes_back_to_the_public_sale()
    {
        await SellAsync(5);
        var gaBefore = (int)await _h.Db.StringGetAsync(_h.Keys.InvGa(_h.EventId, _h.GaTier));
        var messages = await _h.ListenAsync();

        await _h.Store.FreeReturnedAsync(_h.EventId, [5], [new GaLine(_h.GaTier, 2)], Buyer, _h.GaTiers, Ct);

        (await _h.Db.SetContainsAsync(_h.Keys.InvSold(_h.EventId), 5)).ShouldBeFalse();
        ((int)await _h.Db.StringGetAsync(_h.Keys.InvGa(_h.EventId, _h.GaTier))).ShouldBe(gaBefore + 2);
        ((int)await _h.Db.StringGetAsync(_h.Keys.InvBought(_h.EventId, Buyer))).ShouldBe(0);
        await WaitUntilAsync(() => !messages.IsEmpty);
        using var delta = JsonDocument.Parse(messages.Single());
        delta.RootElement.GetProperty("freed").EnumerateArray().Select(e => e.GetInt32()).ShouldBe([5]);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("No delta was published.");
            await Task.Delay(20, Ct);
        }
    }
}
