using TicketRush.Application.Abstractions.Messaging;

namespace TicketRush.Infrastructure.Outbox;

/// <summary>
/// A domain event waiting to be dispatched. Written in the same transaction as the aggregate change (C8).
/// A dead-lettered message has <see cref="ProcessedAt"/> set and keeps its <see cref="LastError"/>.
/// </summary>
public sealed class OutboxMessage
{
    public OutboxMessage(Guid id, string type, string payload, Guid? aggregateId, DateTimeOffset occurredAt)
    {
        Id = id;
        Type = type;
        Payload = payload;
        AggregateId = aggregateId;
        OccurredAt = occurredAt;
        NextAttemptAt = occurredAt;
    }

    public Guid Id { get; private set; }

    public string Type { get; private set; }

    public string Payload { get; private set; }

    /// <summary>Messages of the same aggregate are dispatched strictly in <see cref="OccurredAt"/> order.</summary>
    public Guid? AggregateId { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public DateTimeOffset? ProcessedAt { get; private set; }

    public int Attempts { get; private set; }

    public DateTimeOffset NextAttemptAt { get; private set; }

    public string? LastError { get; private set; }

    public bool IsDeadLettered => ProcessedAt is not null && LastError is not null;

    public void MarkProcessed(DateTimeOffset now)
    {
        Attempts++;
        ProcessedAt = now;
        LastError = null;
    }

    /// <returns><c>true</c> when the message has exhausted its attempts and is now dead-lettered.</returns>
    public bool RecordFailure(DateTimeOffset now, string error, double jitter)
    {
        Attempts++;
        LastError = error.Length > 2000 ? error[..2000] : error;

        if (OutboxRetryPolicy.IsExhausted(Attempts))
        {
            ProcessedAt = now;
            return true;
        }

        NextAttemptAt = now + OutboxRetryPolicy.NextDelay(Attempts, jitter);
        return false;
    }
}

/// <summary>Idempotency guard: a handler runs at most once per message (unique on message + handler).</summary>
public sealed class ConsumerReceipt(Guid messageId, string handler, DateTimeOffset processedAt)
{
    public Guid MessageId { get; private set; } = messageId;

    public string Handler { get; private set; } = handler;

    public DateTimeOffset ProcessedAt { get; private set; } = processedAt;
}
