using System.Text.Json;
using System.Text.RegularExpressions;
using TicketRush.Application.Features.Catalog;
using TicketRush.Infrastructure.Realtime;

namespace TicketRush.Worker.Inventory;

/// <summary>A delta exactly as an inventory script published it.</summary>
public sealed record RawSeatDelta(long V, int[] Held, int[] Freed, int[] Sold, IReadOnlyDictionary<Guid, int> Ga);

/// <summary>
/// Folds the raw deltas of a 250 ms window into one message per event (spec 07). The last change of a seat wins, so a seat
/// held and released inside the window is sent once, as free. Thread-safe: the subscriber adds, the timer drains.
/// </summary>
public sealed partial class DeltaCollapser
{
    private readonly Lock _gate = new();
    private Dictionary<Guid, List<RawSeatDelta>> _pending = [];

    public void Add(Guid eventId, RawSeatDelta delta)
    {
        lock (_gate)
        {
            if (!_pending.TryGetValue(eventId, out var list))
            {
                _pending[eventId] = list = [];
            }

            list.Add(delta);
        }
    }

    public IReadOnlyList<(Guid EventId, SeatDelta Delta)> Drain()
    {
        Dictionary<Guid, List<RawSeatDelta>> window;
        lock (_gate)
        {
            (window, _pending) = (_pending, []);
        }

        return [.. window.Select(e => (e.Key, Fold(e.Value)))];
    }

    private static SeatDelta Fold(List<RawSeatDelta> deltas)
    {
        var seats = new Dictionary<int, char>();
        var tiers = new Dictionary<Guid, int>();
        foreach (var delta in deltas.OrderBy(d => d.V))
        {
            foreach (var seat in delta.Held) { seats[seat] = 'h'; }
            foreach (var seat in delta.Freed) { seats[seat] = 'f'; }
            foreach (var seat in delta.Sold) { seats[seat] = 's'; }
            foreach (var (tier, remaining) in delta.Ga) { tiers[tier] = remaining; }
        }

        int[] Of(char state) => [.. seats.Where(s => s.Value == state).Select(s => s.Key).Order()];
        return new SeatDelta(
            deltas.Min(d => d.V),
            deltas.Max(d => d.V),
            Of('h'),
            Of('f'),
            Of('s'),
            [.. tiers.Select(t => new TierRemaining(t.Key, t.Value))]);
    }

    /// <summary>Reads the event id from <c>…:inv:{evt:&lt;id&gt;}:changes</c> and the script's JSON payload.</summary>
    public static bool TryParse(string channel, string payload, out Guid eventId, out RawSeatDelta delta)
    {
        eventId = Guid.Empty;
        delta = null!;
        var match = EventInChannel().Match(channel);
        if (!match.Success || !Guid.TryParseExact(match.Groups[1].Value, "N", out eventId))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            int[] Ints(string name) => [.. root.GetProperty(name).EnumerateArray().Select(e => e.GetInt32())];
            var ga = root.GetProperty("ga").EnumerateObject().ToDictionary(p => Guid.ParseExact(p.Name, "N"), p => p.Value.GetInt32());
            delta = new RawSeatDelta(root.GetProperty("v").GetInt64(), Ints("held"), Ints("freed"), Ints("sold"), ga);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or FormatException or InvalidOperationException)
        {
            return false;
        }
    }

    [GeneratedRegex(@":inv:\{evt:([0-9a-f]{32})\}:changes$")]
    private static partial Regex EventInChannel();
}
