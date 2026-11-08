using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Application.Abstractions.Inventory;

namespace TicketRush.Api.IntegrationTests.Holds;

/// <summary>inventory_commit: what checkout (spec 08) calls after the database commit.</summary>
public sealed class InventoryCommitTests(ContainersFixture containers) : IAsyncLifetime
{
    private static readonly CancellationToken None = CancellationToken.None;
    private InventoryHarness _h = null!;

    public async ValueTask InitializeAsync() => _h = await InventoryHarness.CreateAsync(containers);

    public async ValueTask DisposeAsync() => await _h.DisposeAsync();

    [Fact]
    public async Task Commit_moves_held_seats_to_sold_and_counts_them_as_bought()
    {
        var user = Guid.NewGuid();
        GaLine[] ga = [new(_h.GaTier, 1)];
        var hold = await _h.Store.CreateHoldAsync(_h.Request(user, [1, 2], ga), None);
        var messages = await _h.ListenAsync();

        await _h.Store.CommitAsync(_h.EventId, Guid.NewGuid(), hold.HoldId!.Value, user, [1, 2], ga, _h.GaTiers, None);

        var snapshot = await _h.Store.GetSnapshotAsync(_h.EventId, _h.GaTiers, None);
        snapshot.Sold.ShouldBe([1, 2]);
        snapshot.Held.ShouldBeEmpty();
        snapshot.GaRemaining[_h.GaTier].ShouldBe(_h.GaCapacity - 1);
        ((int)await _h.Db.StringGetAsync(_h.Keys.InvBought(_h.EventId, user))).ShouldBe(3);
        ((int)await _h.Db.HashGetAsync(_h.Keys.InvGaHeld(_h.EventId), _h.GaTier.ToString("N"))).ShouldBe(0);
        (await _h.Store.GetUserHoldAsync(_h.EventId, user, None)).ShouldBeNull();
        (await _h.Db.SortedSetLengthAsync(_h.Keys.InvExpiry(_h.EventId))).ShouldBe(0);
        await WaitUntilAsync(() => messages.Any(m => m.Contains("\"sold\":[1,2]", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_late_commit_after_expiry_still_sells_and_takes_ga()
    {
        var user = Guid.NewGuid();
        GaLine[] ga = [new(_h.GaTier, 2)];
        var hold = await _h.Store.CreateHoldAsync(_h.Request(user, [1], ga), None);
        await _h.Store.ReleaseHoldAsync(_h.EventId, hold.HoldId!.Value, _h.GaTiers, None);

        await _h.Store.CommitAsync(_h.EventId, Guid.NewGuid(), hold.HoldId!.Value, user, [1], ga, _h.GaTiers, None);

        var snapshot = await _h.Store.GetSnapshotAsync(_h.EventId, _h.GaTiers, None);
        snapshot.Sold.ShouldBe([1]);
        snapshot.GaRemaining[_h.GaTier].ShouldBe(_h.GaCapacity - 2);
        ((int)await _h.Db.StringGetAsync(_h.Keys.InvBought(_h.EventId, user))).ShouldBe(3);
    }

    [Fact]
    public async Task A_redelivered_commit_changes_nothing()
    {
        var user = Guid.NewGuid();
        var order = Guid.NewGuid();
        GaLine[] ga = [new(_h.GaTier, 1)];
        var hold = await _h.Store.CreateHoldAsync(_h.Request(user, [1, 2], ga), None);
        var first = await _h.Store.CommitAsync(_h.EventId, order, hold.HoldId!.Value, user, [1, 2], ga, _h.GaTiers, None);
        var before = await _h.Store.GetSnapshotAsync(_h.EventId, _h.GaTiers, None);
        var messages = await _h.ListenAsync();

        var second = await _h.Store.CommitAsync(_h.EventId, order, hold.HoldId!.Value, user, [1, 2], ga, _h.GaTiers, None);

        first.Status.ShouldBe(CommitStatus.Committed);
        second.Status.ShouldBe(CommitStatus.Duplicate);
        second.Version.ShouldBe(first.Version);
        var after = await _h.Store.GetSnapshotAsync(_h.EventId, _h.GaTiers, None);
        after.Version.ShouldBe(before.Version);
        after.Sold.ShouldBe(before.Sold);
        after.GaRemaining[_h.GaTier].ShouldBe(_h.GaCapacity - 1);
        ((int)await _h.Db.StringGetAsync(_h.Keys.InvBought(_h.EventId, user))).ShouldBe(3);
        (await _h.Store.GetCommitFenceAsync(_h.EventId, None)).ShouldBe(1);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        messages.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_commit_marker_lives_for_seven_days()
    {
        var order = Guid.NewGuid();
        var user = Guid.NewGuid();
        var hold = await _h.HoldAsync(user, 1);

        await _h.Store.CommitAsync(_h.EventId, order, hold.HoldId!.Value, user, [1], [], _h.GaTiers, None);

        var ttl = await _h.Db.KeyTimeToLiveAsync(_h.Keys.InvCommitted(_h.EventId, order));
        ttl.ShouldNotBeNull();
        ttl.Value.ShouldBeInRange(TimeSpan.FromDays(7) - TimeSpan.FromMinutes(1), TimeSpan.FromDays(7));
    }

    [Fact]
    public async Task A_redelivered_late_commit_takes_ga_once()
    {
        var user = Guid.NewGuid();
        var order = Guid.NewGuid();
        GaLine[] ga = [new(_h.GaTier, 2)];
        var hold = await _h.Store.CreateHoldAsync(_h.Request(user, [1], ga), None);
        await _h.Store.ReleaseHoldAsync(_h.EventId, hold.HoldId!.Value, _h.GaTiers, None);

        await _h.Store.CommitAsync(_h.EventId, order, hold.HoldId!.Value, user, [1], ga, _h.GaTiers, None);
        var second = await _h.Store.CommitAsync(_h.EventId, order, hold.HoldId!.Value, user, [1], ga, _h.GaTiers, None);

        second.Status.ShouldBe(CommitStatus.Duplicate);
        var snapshot = await _h.Store.GetSnapshotAsync(_h.EventId, _h.GaTiers, None);
        snapshot.GaRemaining[_h.GaTier].ShouldBe(_h.GaCapacity - 2);
        ((int)await _h.Db.StringGetAsync(_h.Keys.InvBought(_h.EventId, user))).ShouldBe(3);
    }

    [Fact]
    public async Task Committing_a_replaced_hold_takes_ga_from_remaining_and_leaves_the_new_holds_ga_held()
    {
        var user = Guid.NewGuid();
        var old = await _h.Store.CreateHoldAsync(_h.Request(user, [1], [new GaLine(_h.GaTier, 1)]), None);
        var replacement = await _h.Store.CreateHoldAsync(_h.Request(user, [2], [new GaLine(_h.GaTier, 2)], replace: true), None);

        await _h.Store.CommitAsync(_h.EventId, Guid.NewGuid(), old.HoldId!.Value, user, [1], [new GaLine(_h.GaTier, 1)], _h.GaTiers, None);

        // The new hold still owns its 2 GA tickets; the commit takes its own 1 from remaining, exactly once.
        ((int)await _h.Db.HashGetAsync(_h.Keys.InvGaHeld(_h.EventId), _h.GaTier.ToString("N"))).ShouldBe(2);
        var snapshot = await _h.Store.GetSnapshotAsync(_h.EventId, _h.GaTiers, None);
        snapshot.GaRemaining[_h.GaTier].ShouldBe(_h.GaCapacity - 3);
        snapshot.Sold.ShouldBe([1]);
        ((int)await _h.Db.StringGetAsync(_h.Keys.InvBought(_h.EventId, user))).ShouldBe(2);
        (await _h.Store.GetUserHoldAsync(_h.EventId, user, None))!.HoldId.ShouldBe(replacement.HoldId!.Value);
    }

    [Fact]
    public async Task Different_orders_of_the_same_user_each_commit()
    {
        var user = Guid.NewGuid();
        var a = await _h.HoldAsync(user, 1);
        await _h.Store.CommitAsync(_h.EventId, Guid.NewGuid(), a.HoldId!.Value, user, [1], [], _h.GaTiers, None);
        var b = await _h.HoldAsync(user, 2);

        var outcome = await _h.Store.CommitAsync(_h.EventId, Guid.NewGuid(), b.HoldId!.Value, user, [2], [], _h.GaTiers, None);

        outcome.Status.ShouldBe(CommitStatus.Committed);
        ((int)await _h.Db.StringGetAsync(_h.Keys.InvBought(_h.EventId, user))).ShouldBe(2);
    }

    [Fact]
    public async Task Committing_a_seat_that_another_hold_owns_now_sells_it_and_removes_that_holds_claim()
    {
        var late = Guid.NewGuid();
        var other = Guid.NewGuid();
        var expired = await _h.HoldAsync(late, 3);
        await _h.Store.ReleaseHoldAsync(_h.EventId, expired.HoldId!.Value, _h.GaTiers, None);
        var newOwner = await _h.HoldAsync(other, 3);
        var messages = await _h.ListenAsync();

        await _h.Store.CommitAsync(_h.EventId, Guid.NewGuid(), expired.HoldId!.Value, late, [3], [], _h.GaTiers, None);
        await _h.Store.ReleaseHoldAsync(_h.EventId, newOwner.HoldId!.Value, _h.GaTiers, None);

        var snapshot = await _h.Store.GetSnapshotAsync(_h.EventId, _h.GaTiers, None);
        snapshot.Sold.ShouldBe([3]);
        snapshot.Held.ShouldBeEmpty();
        await Task.Delay(100, TestContext.Current.CancellationToken);
        messages.ShouldNotContain(m => m.Contains("\"freed\":[3]", StringComparison.Ordinal), "a sold seat is never announced as free");
    }

    [Fact]
    public async Task Committed_seats_cannot_be_held_again()
    {
        var user = Guid.NewGuid();
        var hold = await _h.HoldAsync(user, 4);
        await _h.Store.CommitAsync(_h.EventId, Guid.NewGuid(), hold.HoldId!.Value, user, [4], [], _h.GaTiers, None);

        var outcome = await _h.HoldAsync(Guid.NewGuid(), 4);

        outcome.Status.ShouldBe(HoldStatus.SeatUnavailable);
        outcome.Unavailable.ShouldBe([4]);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        condition().ShouldBeTrue();
    }
}
