using System.Text.Json;
using StackExchange.Redis;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Application.Abstractions.Inventory;
using TicketRush.Infrastructure.Inventory;

namespace TicketRush.Api.IntegrationTests.Holds;

/// <summary>hold_create / hold_release against a real Redis (spec 07, C1, C9).</summary>
public sealed class HoldScriptTests(ContainersFixture containers) : IAsyncLifetime
{
    private InventoryHarness _h = null!;

    public async ValueTask InitializeAsync() => _h = await InventoryHarness.CreateAsync(containers);

    public async ValueTask DisposeAsync() => await _h.DisposeAsync();

    [Fact]
    public async Task Free_seats_are_held_and_published_with_a_new_version()
    {
        var messages = await _h.ListenAsync();

        var outcome = await _h.HoldAsync(Guid.NewGuid(), 3, 4);

        outcome.Status.ShouldBe(HoldStatus.Ok);
        outcome.ExpiresAt.ShouldNotBeNull().ShouldBe(_h.Time.GetUtcNow().AddMinutes(8), TimeSpan.FromMilliseconds(1));
        (await _h.OwnerOfAsync(3)).ShouldBe(outcome.HoldId!.Value.ToString("N"));
        (await _h.OwnerOfAsync(4)).ShouldBe(outcome.HoldId!.Value.ToString("N"));
        ((long)await _h.Db.StringGetAsync(_h.Keys.InvVersion(_h.EventId))).ShouldBe(1);

        await WaitUntilAsync(() => !messages.IsEmpty);
        using var delta = JsonDocument.Parse(messages.Single());
        delta.RootElement.GetProperty("v").GetInt64().ShouldBe(1);
        delta.RootElement.GetProperty("held").EnumerateArray().Select(e => e.GetInt32()).ShouldBe([3, 4]);
        delta.RootElement.GetProperty("freed").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task A_thousand_concurrent_claims_for_one_seat_yield_exactly_one_ok()
    {
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 1000).Select(_ => Task.Run(() => _h.HoldAsync(Guid.NewGuid(), 7))));

        outcomes.Count(o => o.Status == HoldStatus.Ok).ShouldBe(1);
        outcomes.Where(o => o.Status != HoldStatus.Ok).ShouldAllBe(o => o.Status == HoldStatus.SeatUnavailable && o.Unavailable.SequenceEqual(new[] { 7 }));
        (await _h.OwnerOfAsync(7)).ShouldBe(outcomes.Single(o => o.Status == HoldStatus.Ok).HoldId!.Value.ToString("N"));
    }

    [Fact]
    public async Task A_partially_taken_selection_writes_nothing()
    {
        await _h.HoldAsync(Guid.NewGuid(), 2);

        var outcome = await _h.HoldAsync(Guid.NewGuid(), 1, 2);

        outcome.Status.ShouldBe(HoldStatus.SeatUnavailable);
        outcome.Unavailable.ShouldBe([2]);
        (await _h.OwnerOfAsync(1)).ShouldBeNull();
        ((long)await _h.Db.StringGetAsync(_h.Keys.InvVersion(_h.EventId))).ShouldBe(1);
    }

    [Fact]
    public async Task Replacing_keeps_overlapping_seats_held_throughout()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        (await _h.HoldAsync(a, 1, 2)).Status.ShouldBe(HoldStatus.Ok);

        for (var i = 0; i < 200; i++)
        {
            int[] next = i % 2 == 0 ? [2, 3] : [1, 2];
            var replace = Task.Run(() => _h.Store.CreateHoldAsync(_h.Request(a, next, replace: true), CancellationToken.None));
            var steal = Task.Run(() => _h.HoldAsync(b, 2));
            (await replace).Status.ShouldBe(HoldStatus.Ok);
            (await steal).Status.ShouldNotBe(HoldStatus.Ok, $"seat 2 was free during replace #{i}");
        }

        var current = (await _h.Store.GetUserHoldAsync(_h.EventId, a, CancellationToken.None)).ShouldNotBeNull();
        current.Seats.ShouldBe([1, 2]);
        (await _h.OwnerOfAsync(2)).ShouldBe(current.HoldId.ToString("N"));
        (await _h.OwnerOfAsync(3)).ShouldBeNull();
    }

    [Fact]
    public async Task A_stale_userhold_read_is_refused_without_writing()
    {
        var user = Guid.NewGuid();
        (await _h.HoldAsync(user, 1)).Status.ShouldBe(HoldStatus.Ok);

        var result = await InventoryScripts.CreateAsync(_h.Db, _h.Keys, _h.Request(user, [5], replace: true), Guid.NewGuid(), expectedOld: Guid.NewGuid());

        ((string)result[0]!).ShouldBe("STALE");
        (await _h.OwnerOfAsync(5)).ShouldBeNull();
    }

    [Fact]
    public async Task Two_tabs_replacing_at_once_leave_one_hold_and_no_orphaned_seats()
    {
        var user = Guid.NewGuid();
        (await _h.HoldAsync(user, 1)).Status.ShouldBe(HoldStatus.Ok);

        for (var i = 0; i < 50; i++)
        {
            int[] first = [10 + (i % 5)];
            int[] second = [20 + (i % 5)];
            await Task.WhenAll(
                Task.Run(() => _h.Store.CreateHoldAsync(_h.Request(user, first, replace: true), CancellationToken.None)),
                Task.Run(() => _h.Store.CreateHoldAsync(_h.Request(user, second, replace: true), CancellationToken.None)));
        }

        var hold = (await _h.Store.GetUserHoldAsync(_h.EventId, user, CancellationToken.None)).ShouldNotBeNull();
        var held = await _h.Db.HashGetAllAsync(_h.Keys.InvHeld(_h.EventId));
        held.Select(f => (int)f.Name).ShouldBe(hold.Seats);
        held.ShouldAllBe(f => f.Value == hold.HoldId.ToString("N"));
        (await _h.Db.SortedSetLengthAsync(_h.Keys.InvExpiry(_h.EventId))).ShouldBe(1);
    }

    [Fact]
    public async Task Second_hold_without_replace_is_HasHold()
    {
        var user = Guid.NewGuid();
        await _h.HoldAsync(user, 1);

        (await _h.HoldAsync(user, 2)).Status.ShouldBe(HoldStatus.HasHold);
        (await _h.OwnerOfAsync(2)).ShouldBeNull();
    }

    [Fact]
    public async Task An_expired_own_hold_does_not_block_a_new_one()
    {
        var user = Guid.NewGuid();
        await _h.HoldAsync(user, 1);
        _h.Time.Advance(TimeSpan.FromMinutes(9));

        var outcome = await _h.HoldAsync(user, 1, 2);

        outcome.Status.ShouldBe(HoldStatus.Ok);
        (await _h.OwnerOfAsync(1)).ShouldBe(outcome.HoldId!.Value.ToString("N"));
    }

    [Fact]
    public async Task Ga_holds_decrement_remaining_and_reject_over_claim()
    {
        var first = await _h.Store.CreateHoldAsync(_h.Request(Guid.NewGuid(), ga: [new GaLine(_h.GaTier, 3)]), CancellationToken.None);
        var second = await _h.Store.CreateHoldAsync(_h.Request(Guid.NewGuid(), ga: [new GaLine(_h.GaTier, 3)]), CancellationToken.None);

        first.Status.ShouldBe(HoldStatus.Ok);
        second.Status.ShouldBe(HoldStatus.GaInsufficient);
        second.Tier.ShouldBe(_h.GaTier);
        ((int)await _h.Db.StringGetAsync(_h.Keys.InvGa(_h.EventId, _h.GaTier))).ShouldBe(2);
        ((int)await _h.Db.HashGetAsync(_h.Keys.InvGaHeld(_h.EventId), _h.GaTier.ToString("N"))).ShouldBe(3);
    }

    [Fact]
    public async Task Replacing_a_ga_hold_counts_the_old_quantity_as_free()
    {
        var user = Guid.NewGuid();
        await _h.Store.CreateHoldAsync(_h.Request(user, ga: [new GaLine(_h.GaTier, 4)]), CancellationToken.None);

        var outcome = await _h.Store.CreateHoldAsync(_h.Request(user, ga: [new GaLine(_h.GaTier, 5)], replace: true), CancellationToken.None);

        outcome.Status.ShouldBe(HoldStatus.Ok);
        ((int)await _h.Db.StringGetAsync(_h.Keys.InvGa(_h.EventId, _h.GaTier))).ShouldBe(0);
    }

    [Fact]
    public async Task Limit_counts_bought_plus_requested()
    {
        var user = Guid.NewGuid();
        await _h.Db.StringSetAsync(_h.Keys.InvBought(_h.EventId, user), 3);

        var outcome = await _h.Store.CreateHoldAsync(_h.Request(user, [1, 2], maxTickets: 4), CancellationToken.None);

        outcome.Status.ShouldBe(HoldStatus.LimitExceeded);
        (await _h.OwnerOfAsync(1)).ShouldBeNull();
    }

    [Fact]
    public async Task Concurrent_holds_of_one_buyer_never_hold_more_than_the_cap_together()
    {
        // Spec 12: a buyer with 2 bought tickets and a cap of 4 fires many holds at once, each within the cap on its own but over
        // it together. One hold per account is enforced inside the same script, so exactly one is written.
        var user = Guid.NewGuid();
        await _h.Db.StringSetAsync(_h.Keys.InvBought(_h.EventId, user), 2);
        var selections = Enumerable.Range(0, 20).Select(i => new[] { 10 + (2 * i), 11 + (2 * i) }).ToArray();

        var outcomes = await Task.WhenAll(selections.Select(seats =>
            Task.Run(() => _h.Store.CreateHoldAsync(_h.Request(user, seats, maxTickets: 4), CancellationToken.None))));

        outcomes.Count(o => o.Status == HoldStatus.Ok).ShouldBe(1);
        var held = await _h.Db.HashGetAllAsync(_h.Keys.InvHeld(_h.EventId));
        held.Length.ShouldBe(2, "2 bought + 2 held = the cap of 4");
        (await _h.Store.CreateHoldAsync(_h.Request(user, [90, 91, 92], maxTickets: 4, replace: true), CancellationToken.None))
            .Status.ShouldBe(HoldStatus.LimitExceeded, "a replacement is checked against the cap too");
    }

    [Fact]
    public async Task Releasing_frees_only_seats_still_owned_by_the_hold()
    {
        var a = await _h.HoldAsync(Guid.NewGuid(), 5, 6);
        // Simulate a's seat 5 having expired and been taken by b in the meantime.
        await _h.Db.HashDeleteAsync(_h.Keys.InvHeld(_h.EventId), 5);
        var b = await _h.HoldAsync(Guid.NewGuid(), 5);

        (await _h.Store.ReleaseHoldAsync(_h.EventId, a.HoldId!.Value, _h.GaTiers, CancellationToken.None)).ShouldBeTrue();

        (await _h.OwnerOfAsync(5)).ShouldBe(b.HoldId!.Value.ToString("N"));
        (await _h.OwnerOfAsync(6)).ShouldBeNull();
    }

    [Fact]
    public async Task Replacing_a_hold_never_announces_a_dropped_sold_seat_as_freed()
    {
        var user = Guid.NewGuid();
        await _h.HoldAsync(user, 1, 2);
        await _h.Db.SetAddAsync(_h.Keys.InvSold(_h.EventId), 1);
        var messages = await _h.ListenAsync();

        var outcome = await _h.Store.CreateHoldAsync(_h.Request(user, [2, 3], replace: true), CancellationToken.None);

        outcome.Status.ShouldBe(HoldStatus.Ok);
        await WaitUntilAsync(() => !messages.IsEmpty);
        using var delta = JsonDocument.Parse(messages.Single());
        delta.RootElement.GetProperty("freed").GetArrayLength().ShouldBe(0);
        (await _h.OwnerOfAsync(1)).ShouldBeNull();
    }

    [Fact]
    public async Task Releasing_a_hold_never_announces_a_sold_seat_as_freed()
    {
        var hold = await _h.HoldAsync(Guid.NewGuid(), 5, 6);
        // A reconcile that read a checkout the hold belongs to can leave a seat both held and sold.
        await _h.Db.SetAddAsync(_h.Keys.InvSold(_h.EventId), 5);
        var messages = await _h.ListenAsync();

        (await _h.Store.ReleaseHoldAsync(_h.EventId, hold.HoldId!.Value, _h.GaTiers, CancellationToken.None)).ShouldBeTrue();

        await WaitUntilAsync(() => !messages.IsEmpty);
        using var delta = JsonDocument.Parse(messages.Single());
        delta.RootElement.GetProperty("freed").EnumerateArray().Select(e => e.GetInt32()).ShouldBe([6]);
        (await _h.OwnerOfAsync(5)).ShouldBeNull();
        (await _h.Db.SetContainsAsync(_h.Keys.InvSold(_h.EventId), 5)).ShouldBeTrue();
    }

    [Fact]
    public async Task Releasing_returns_ga_and_clears_the_users_hold()
    {
        var user = Guid.NewGuid();
        var hold = await _h.Store.CreateHoldAsync(_h.Request(user, [1], [new GaLine(_h.GaTier, 2)]), CancellationToken.None);

        (await _h.Store.ReleaseHoldAsync(_h.EventId, hold.HoldId!.Value, _h.GaTiers, CancellationToken.None)).ShouldBeTrue();

        ((int)await _h.Db.StringGetAsync(_h.Keys.InvGa(_h.EventId, _h.GaTier))).ShouldBe(_h.GaCapacity);
        (await _h.Store.GetUserHoldAsync(_h.EventId, user, CancellationToken.None)).ShouldBeNull();
        (await _h.Db.SortedSetLengthAsync(_h.Keys.InvExpiry(_h.EventId))).ShouldBe(0);
    }

    [Fact]
    public async Task Releasing_an_unknown_hold_returns_false_and_changes_nothing()
    {
        (await _h.Store.ReleaseHoldAsync(_h.EventId, Guid.NewGuid(), _h.GaTiers, CancellationToken.None)).ShouldBeFalse();

        (await _h.Db.KeyExistsAsync(_h.Keys.InvVersion(_h.EventId))).ShouldBeFalse();
    }

    [Fact]
    public async Task Seat_indexes_are_stored_sorted()
    {
        var outcome = await _h.HoldAsync(Guid.NewGuid(), 9, 3, 7);

        ((string?)await _h.Db.HashGetAsync(_h.Keys.InvHold(_h.EventId, outcome.HoldId!.Value), "seats")).ShouldBe("3,7,9");
    }

    [Fact]
    public async Task Due_holds_are_listed_after_their_expiry()
    {
        var hold = await _h.HoldAsync(Guid.NewGuid(), 1);

        (await _h.Store.DueHoldsAsync(_h.EventId, _h.Time.GetUtcNow(), 10, CancellationToken.None)).ShouldBeEmpty();
        (await _h.Store.DueHoldsAsync(_h.EventId, _h.Time.GetUtcNow().AddMinutes(8), 10, CancellationToken.None)).ShouldBe([hold.HoldId!.Value]);
    }

    [Fact]
    public async Task Snapshot_reports_held_sold_ga_and_version()
    {
        await _h.Store.CreateHoldAsync(_h.Request(Guid.NewGuid(), [4, 2], [new GaLine(_h.GaTier, 1)]), CancellationToken.None);
        await _h.Db.SetAddAsync(_h.Keys.InvSold(_h.EventId), 9);

        var snapshot = await _h.Store.GetSnapshotAsync(_h.EventId, _h.GaTiers, CancellationToken.None);

        snapshot.Held.ShouldBe([2, 4]);
        snapshot.Sold.ShouldBe([9]);
        snapshot.GaRemaining[_h.GaTier].ShouldBe(_h.GaCapacity - 1);
        snapshot.Version.ShouldBe(1);
        snapshot.Warm.ShouldBeTrue();
    }

    [Fact]
    public async Task Redis_errors_map_to_InventoryUnavailable()
    {
        Exception[] failures =
        [
            new RedisTimeoutException("Timeout awaiting response", CommandStatus.Unknown),
            new RedisConnectionException(ConnectionFailureType.SocketFailure, "socket closed"),
            new ObjectDisposedException("multiplexer"),
        ];

        foreach (var failure in failures)
        {
            var thrown = await Should.ThrowAsync<InventoryUnavailableException>(() => RedisInventoryStore.GuardAsync<int>(() => throw failure));
            thrown.InnerException.ShouldBe(failure);
        }

        await Should.ThrowAsync<InvalidOperationException>(() => RedisInventoryStore.GuardAsync<int>(() => throw new InvalidOperationException()));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
    }
}
