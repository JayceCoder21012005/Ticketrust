using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TicketRush.Application.Abstractions.Messaging;
using TicketRush.Infrastructure.Persistence;

namespace TicketRush.Infrastructure.Outbox;

/// <summary>
/// Claims a batch of pending outbox messages with <c>FOR UPDATE SKIP LOCKED</c> (so several worker
/// instances never take the same row) and runs their handlers.
/// <para>
/// Each handler runs in its own savepoint on the batch transaction, through a fresh <see cref="AppDbContext"/>
/// that shares the connection. The handler's DB writes and its <c>consumer_receipts</c> row therefore commit
/// atomically with the message state: a crash before commit rolls everything back and the message is
/// re-delivered; after commit the receipt stops the handler from running again (C8, C12).
/// </para>
/// </summary>
public sealed partial class OutboxProcessor(
    IServiceScopeFactory scopeFactory,
    DomainEventTypeRegistry typeRegistry,
    IEnumerable<IntegrationEventHandlerRegistration> registrations,
    OutboxMetrics metrics,
    TimeProvider timeProvider,
    IOptions<OutboxOptions> options,
    ILogger<OutboxProcessor> logger)
{
    public static readonly ActivitySource ActivitySource = new("TicketRush.Outbox");

    private const string HandlerSavepoint = "outbox_handler";

    private readonly ILookup<string, IntegrationEventHandlerRegistration> _handlersByType =
        registrations.ToLookup(r => DomainEventTypeRegistry.NameOf(r.EventType), StringComparer.Ordinal);

    /// <returns>The number of messages claimed in this batch (0 = nothing to do).</returns>
    public async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var messages = await ClaimBatchAsync(db, ct);
        foreach (var message in messages)
        {
            await ProcessMessageAsync(db, transaction, message, ct);
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return messages.Count;
    }

    private Task<List<OutboxMessage>> ClaimBatchAsync(AppDbContext db, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        var batchSize = options.Value.BatchSize;

        // The NOT EXISTS clause keeps per-aggregate order: a message is only eligible when no older message
        // of the same aggregate is still pending (even one locked by another worker or waiting for a retry).
        return db.OutboxMessages
            .FromSql($"""
                SELECT m.* FROM outbox_messages m
                WHERE m.processed_at IS NULL
                  AND m.next_attempt_at <= {now}
                  AND NOT EXISTS (
                      SELECT 1 FROM outbox_messages older
                      WHERE older.aggregate_id = m.aggregate_id
                        AND older.processed_at IS NULL
                        AND (older.occurred_at, older.id) < (m.occurred_at, m.id))
                ORDER BY m.occurred_at, m.id
                LIMIT {batchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(ct);
    }

    private async Task ProcessMessageAsync(
        AppDbContext db,
        IDbContextTransaction transaction,
        OutboxMessage message,
        CancellationToken ct)
    {
        using var activity = ActivitySource.StartActivity($"outbox {message.Type}", ActivityKind.Consumer);
        activity?.SetTag("messaging.message.id", message.Id);

        try
        {
            var domainEvent = typeRegistry.Deserialize(message);
            var context = new IntegrationEventContext(message.Id, message.OccurredAt, message.Attempts + 1);

            foreach (var registration in _handlersByType[message.Type])
            {
                await RunHandlerOnceAsync(db, transaction, registration, domainEvent, context, ct);
            }

            message.MarkProcessed(timeProvider.GetUtcNow());
            metrics.Processed(message.Type);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            RecordFailure(message, ex);
        }
    }

    private async Task RunHandlerOnceAsync(
        AppDbContext db,
        IDbContextTransaction transaction,
        IntegrationEventHandlerRegistration registration,
        object domainEvent,
        IntegrationEventContext context,
        CancellationToken ct)
    {
        var alreadyHandled = await db.ConsumerReceipts.AnyAsync(
            r => r.MessageId == context.MessageId && r.Handler == registration.HandlerName, ct);
        if (alreadyHandled)
        {
            return;
        }

        await transaction.CreateSavepointAsync(HandlerSavepoint, ct);
        try
        {
            await using var handlerScope = scopeFactory.CreateAsyncScope();
            var handlerDb = await JoinTransactionAsync(handlerScope.ServiceProvider, db, transaction, ct);

            await registration.Invoke(handlerScope.ServiceProvider, domainEvent, context, ct);

            handlerDb.ConsumerReceipts.Add(new ConsumerReceipt(context.MessageId, registration.HandlerName, timeProvider.GetUtcNow()));
            await handlerDb.SaveChangesAsync(ct);
            await transaction.ReleaseSavepointAsync(HandlerSavepoint, ct);
        }
        catch
        {
            await transaction.RollbackToSavepointAsync(HandlerSavepoint, CancellationToken.None);
            throw;
        }
    }

    private static async Task<AppDbContext> JoinTransactionAsync(
        IServiceProvider services,
        AppDbContext batchDb,
        IDbContextTransaction transaction,
        CancellationToken ct)
    {
        var handlerDb = services.GetRequiredService<AppDbContext>();
        handlerDb.Database.SetDbConnection(batchDb.Database.GetDbConnection(), contextOwnsConnection: false);
        await handlerDb.Database.UseTransactionAsync(transaction.GetDbTransaction(), ct);
        return handlerDb;
    }

    private void RecordFailure(OutboxMessage message, Exception error)
    {
        var deadLettered = message.RecordFailure(timeProvider.GetUtcNow(), $"{error.GetType().Name}: {error.Message}", Random.Shared.NextDouble());

        if (deadLettered)
        {
            metrics.DeadLettered(message.Type);
            LogDeadLettered(logger, message.Id, message.Type, message.Attempts, error);
        }
        else
        {
            metrics.Failed(message.Type);
            LogRetryScheduled(logger, message.Id, message.Type, message.Attempts, message.NextAttemptAt, error);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox message {MessageId} ({Type}) dead-lettered after {Attempts} attempts")]
    private static partial void LogDeadLettered(ILogger logger, Guid messageId, string type, int attempts, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox message {MessageId} ({Type}) failed attempt {Attempts}; retry at {NextAttemptAt}")]
    private static partial void LogRetryScheduled(ILogger logger, Guid messageId, string type, int attempts, DateTimeOffset nextAttemptAt, Exception exception);
}
