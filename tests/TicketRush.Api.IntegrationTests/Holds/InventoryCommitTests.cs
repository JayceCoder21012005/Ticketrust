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

        await _h.Store.CommitAsync(_h.EventId, hold.HoldId!.Value, user, [1, 2], ga, _h.GaTiers, None);

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

        await _h.Store.CommitAsync(_h.EventId, hold.HoldId!.Value, user, [1], ga, _h.GaTiers, None);

        var snapshot = await _h.Store.GetSnapshotAsync(_h.EventId, _h.GaTiers, None);
        snapshot.Sold.ShouldBe([1]);
        snapshot.GaRemaining[_h.GaTier].ShouldBe(_h.GaCapacity - 2);
        ((int)await _h.Db.StringGetAsync(_h.Keys.InvBought(_h.EventId, user))).ShouldBe(3);
    }

    [Fact]
    public async Task Committed_seats_cannot_be_held_again()
    {
        var user = Guid.NewGuid();
        var hold = await _h.HoldAsync(user, 4);
        await _h.Store.CommitAsync(_h.EventId, hold.HoldId!.Value, user, [4], [], _h.GaTiers, None);

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
