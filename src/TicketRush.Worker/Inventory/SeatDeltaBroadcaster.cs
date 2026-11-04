using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;
using TicketRush.Application.Abstractions.Locking;
using TicketRush.Infrastructure.Realtime;
using TicketRush.Infrastructure.Redis;
using TicketRush.Worker.Common;

namespace TicketRush.Worker.Inventory;

/// <summary>
/// Subscribes to every event's inventory channel and, every 250 ms, sends one collapsed <see cref="SeatDelta"/> per changed
/// event to its SignalR group through the Redis backplane (spec 07). Leader-elected, so each delta goes out once. Messages
/// published while no leader listens are lost; clients notice the version gap and refetch availability.
/// </summary>
public sealed partial class SeatDeltaBroadcaster(
    IConnectionMultiplexer redis,
    RedisKeys keys,
    IHubContext<EventsHub> hub,
    IDistributedLock distributedLock,
    TimeProvider time,
    ILogger<SeatDeltaBroadcaster> logger)
    : LeaderElectedBackgroundService(distributedLock, time, logger)
{
    public static readonly TimeSpan Window = TimeSpan.FromMilliseconds(250);

    private readonly TimeProvider _clock = time;
    private readonly ILogger _logger = logger;

    protected override string LockName => "seat-delta-broadcaster";

    protected override TimeSpan LockTtl => TimeSpan.FromSeconds(6);

    protected override TimeSpan RetryInterval => TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsLeaderAsync(CancellationToken ct)
    {
        var collapser = new DeltaCollapser();
        var subscriber = redis.GetSubscriber();
        var channel = await subscriber.SubscribeAsync(keys.InvChangesPattern);
        channel.OnMessage(message =>
        {
            if (DeltaCollapser.TryParse(message.Channel.ToString(), message.Message.ToString(), out var eventId, out var delta))
            {
                collapser.Add(eventId, delta);
            }
        });

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(Window, _clock, ct);
                await FlushAsync(collapser, ct);
            }
        }
        finally
        {
            await channel.UnsubscribeAsync();
        }
    }

    private async Task FlushAsync(DeltaCollapser collapser, CancellationToken ct)
    {
        foreach (var (eventId, delta) in collapser.Drain())
        {
            try
            {
                await hub.Clients.Group(EventsHub.Group(eventId)).SendAsync(EventsHub.SeatDeltaMethod, delta, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The backplane drops messages while Redis is down; clients resync on the version gap.
                LogSendFailed(_logger, eventId, ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Seat delta for event {EventId} could not be sent")]
    private static partial void LogSendFailed(ILogger logger, Guid eventId, Exception exception);
}
