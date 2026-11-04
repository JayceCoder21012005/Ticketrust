extern alias WorkerAssembly;

using WorkerAssembly::TicketRush.Worker.Inventory;

namespace TicketRush.Api.IntegrationTests.Holds;

/// <summary>Collapsing raw script deltas into one SignalR message per event per 250 ms window (spec 07).</summary>
public sealed class DeltaCollapserTests
{
    private static readonly Guid Event = Guid.NewGuid();
    private static readonly Guid Tier = Guid.NewGuid();

    private static RawSeatDelta Raw(long v, int[]? held = null, int[]? freed = null, int[]? sold = null, int? ga = null) =>
        new(v, held ?? [], freed ?? [], sold ?? [], ga is null ? [] : new Dictionary<Guid, int> { [Tier] = ga.Value });

    [Fact]
    public void Held_then_released_inside_a_window_sends_only_the_final_state()
    {
        var collapser = new DeltaCollapser();
        collapser.Add(Event, Raw(1, held: [1, 2]));
        collapser.Add(Event, Raw(2, freed: [1]));
        collapser.Add(Event, Raw(3, freed: [3]));
        collapser.Add(Event, Raw(4, held: [3]));

        var delta = collapser.Drain().Single().Delta;

        delta.Held.ShouldBe([2, 3]);
        delta.Freed.ShouldBe([1]);
        delta.Sold.ShouldBeEmpty();
    }

    [Fact]
    public void The_window_carries_its_first_and_last_version()
    {
        var collapser = new DeltaCollapser();
        collapser.Add(Event, Raw(8, held: [1]));
        collapser.Add(Event, Raw(7, held: [2]));

        var delta = collapser.Drain().Single().Delta;

        delta.From.ShouldBe(7);
        delta.V.ShouldBe(8);
    }

    [Fact]
    public void The_latest_ga_remaining_wins()
    {
        var collapser = new DeltaCollapser();
        collapser.Add(Event, Raw(1, ga: 10));
        collapser.Add(Event, Raw(2, ga: 7));

        collapser.Drain().Single().Delta.Tiers.Single().ShouldBe(new TicketRush.Application.Features.Catalog.TierRemaining(Tier, 7));
    }

    [Fact]
    public void Events_are_kept_apart_and_a_drain_empties_the_window()
    {
        var collapser = new DeltaCollapser();
        var other = Guid.NewGuid();
        collapser.Add(Event, Raw(1, held: [1]));
        collapser.Add(other, Raw(5, sold: [9]));

        collapser.Drain().Select(d => d.EventId).ShouldBe([Event, other], ignoreOrder: true);
        collapser.Drain().ShouldBeEmpty();
    }

    [Fact]
    public void Script_payloads_and_channels_are_parsed()
    {
        var channel = $"tr:test:inv:{{evt:{Event:N}}}:changes";
        var payload = "{\"v\":3,\"held\":[4,5],\"freed\":[],\"sold\":[1],\"ga\":{\"" + Tier.ToString("N") + "\":12}}";

        DeltaCollapser.TryParse(channel, payload, out var eventId, out var raw).ShouldBeTrue();

        eventId.ShouldBe(Event);
        raw.V.ShouldBe(3);
        raw.Held.ShouldBe([4, 5]);
        raw.Sold.ShouldBe([1]);
        raw.Ga[Tier].ShouldBe(12);
        DeltaCollapser.TryParse("tr:test:inv:nonsense", payload, out _, out _).ShouldBeFalse();
    }
}
