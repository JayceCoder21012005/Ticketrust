using Microsoft.Extensions.Options;
using TicketRush.Application.Abstractions.Inventory;
using TicketRush.Application.Options;

namespace TicketRush.Worker.Inventory;

/// <summary>
/// Events the inventory workers act on (a phase open or opening within the pre-queue window), refreshed from Postgres at
/// most every few seconds and shared by the sweeper, reconciler and status service, so their loops cost no queries.
/// </summary>
public sealed class LiveInventoryEvents(IInventoryEventSource source, IOptions<QueueOptions> options, TimeProvider time) : IDisposable
{
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<LiveInventoryEvent> _events = [];
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;

    public void Dispose() => _gate.Dispose();

    public async Task<IReadOnlyList<LiveInventoryEvent>> GetAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var now = time.GetUtcNow();
            if (now - _loadedAt >= RefreshEvery || now < _loadedAt)
            {
                _events = await source.ListLiveAsync(now, TimeSpan.FromMinutes(options.Value.PreQueueMinutes), ct);
                _loadedAt = now;
            }

            return _events;
        }
        finally
        {
            _gate.Release();
        }
    }
}
