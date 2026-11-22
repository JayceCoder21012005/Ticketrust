using System.Collections.Concurrent;
using TicketRush.Application.Abstractions.Observability;

namespace TicketRush.Api.IntegrationTests.Infrastructure;

/// <summary>Remembers what the workers recorded, so a test can assert the spec 17 metrics without a meter listener.</summary>
public sealed class RecordingSalesMetrics : ISalesMetrics
{
    private int _expired;
    private int _admitted;
    private int _drift;

    public ConcurrentQueue<string> Holds { get; } = new();

    public ConcurrentDictionary<Guid, (long Length, long Active)> Queues { get; } = new();

    public ConcurrentQueue<Guid> Closed { get; } = new();

    public (double Lag, long Dead)? Outbox { get; private set; }

    public int Expired => _expired;

    public int AdmittedTotal => _admitted;

    public int Drift => _drift;

    public void HoldCreated(string result) => Holds.Enqueue(result);

    public void HoldsExpired(int count) => Interlocked.Add(ref _expired, count);

    public void Admitted(int count) => Interlocked.Add(ref _admitted, count);

    public void QueueObserved(Guid phaseId, long length, long activeShoppers) => Queues[phaseId] = (length, activeShoppers);

    public void QueueClosed(Guid phaseId) => Closed.Enqueue(phaseId);

    public void OrderPaid()
    {
    }

    public void RefundRequested(string reason)
    {
    }

    public void GatewayCall(string operation, TimeSpan duration, bool succeeded)
    {
    }

    public void OutboxObserved(double lagSeconds, long deadLetters) => Outbox = (lagSeconds, deadLetters);

    public void DriftFixed(int corrections) => Interlocked.Add(ref _drift, corrections);
}
