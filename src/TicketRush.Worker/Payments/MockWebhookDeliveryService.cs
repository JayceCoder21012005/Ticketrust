using TicketRush.Application.Abstractions.Locking;
using TicketRush.Infrastructure.Payments;
using TicketRush.Worker.Common;

namespace TicketRush.Worker.Payments;

/// <summary>
/// Dev/demo only (<c>Payments:Mock:Enabled</c>). Every second, sends the mock payment gateway's due webhooks to the api.
/// Leader-elected so a delivery is not sent by two workers at once; the queue's lease keeps a failover safe, and the
/// webhook handler dedupes by provider event id anyway.
/// </summary>
public sealed class MockWebhookDeliveryService(
    MockWebhookDeliverer deliverer,
    IDistributedLock distributedLock,
    TimeProvider time,
    ILogger<MockWebhookDeliveryService> logger)
    : LeaderElectedBackgroundService(distributedLock, time, logger)
{
    private readonly TimeProvider _clock = time;

    protected override string LockName => "mock-webhook-delivery";

    protected override TimeSpan LockTtl => TimeSpan.FromSeconds(6);

    protected override TimeSpan RetryInterval => TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsLeaderAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await deliverer.RunOnceAsync(ct);
            await Task.Delay(TimeSpan.FromSeconds(1), _clock, ct);
        }
    }
}
