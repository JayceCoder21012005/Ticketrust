using System.ComponentModel.DataAnnotations;
using System.Diagnostics.Metrics;

namespace TicketRush.Infrastructure.Outbox;

public sealed class OutboxMetrics
{
    public const string MeterName = "TicketRush.Outbox";

    private readonly Counter<long> _processed;
    private readonly Counter<long> _failed;
    private readonly Counter<long> _deadLettered;

    public OutboxMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _processed = meter.CreateCounter<long>("ticketrush.outbox.processed", description: "Outbox messages handled successfully.");
        _failed = meter.CreateCounter<long>("ticketrush.outbox.failed", description: "Failed attempts that will be retried.");
        _deadLettered = meter.CreateCounter<long>("ticketrush.outbox.dead_lettered", description: "Messages that exhausted their retries.");
    }

    public void Processed(string type) => _processed.Add(1, new KeyValuePair<string, object?>("type", type));

    public void Failed(string type) => _failed.Add(1, new KeyValuePair<string, object?>("type", type));

    public void DeadLettered(string type) => _deadLettered.Add(1, new KeyValuePair<string, object?>("type", type));
}

public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    [Range(1, 1000)]
    public int BatchSize { get; set; } = 100;

    [Range(typeof(TimeSpan), "00:00:00.050", "00:01:00")]
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);
}
