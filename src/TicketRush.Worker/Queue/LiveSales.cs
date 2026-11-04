using Microsoft.Extensions.Options;
using TicketRush.Application.Abstractions.Queue;
using TicketRush.Application.Options;

namespace TicketRush.Worker.Queue;

/// <summary>
/// The sales the queue workers act on, refreshed from Postgres at most every few seconds and shared by the opener and
/// the admitter, so the 1 s loops cost no database round trips.
/// </summary>
public sealed class LiveSales(IQueuePhaseSource source, IOptions<QueueOptions> options, TimeProvider time) : IDisposable
{
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<SaleWindow> _sales = [];
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;

    public void Dispose() => _gate.Dispose();

    public async Task<IReadOnlyList<SaleWindow>> GetAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var now = time.GetUtcNow();
            if (now - _loadedAt >= RefreshEvery || now < _loadedAt)
            {
                _sales = await source.ListLiveAsync(now, TimeSpan.FromMinutes(options.Value.PreQueueMinutes), ct);
                _loadedAt = now;
            }

            return _sales;
        }
        finally
        {
            _gate.Release();
        }
    }
}
