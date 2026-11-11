using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TicketRush.Application.Abstractions.Messaging;
using TicketRush.Application.Abstractions.Notifications;
using TicketRush.Domain.Notifications;
using TicketRush.Infrastructure.Options;
using TicketRush.Infrastructure.Persistence;

namespace TicketRush.Infrastructure.Notifications;

/// <summary>
/// Sends the due <see cref="EmailDeliveryStatus.Pending"/> deliveries. A batch is claimed with <c>FOR UPDATE SKIP LOCKED</c> in
/// a transaction, so several worker instances never send the same row; each row is sent in its own try/catch, so one failing
/// address only reschedules itself (<see cref="OutboxRetryPolicy"/> backoff, parked after <see cref="OutboxRetryPolicy.MaxAttempts"/>).
/// <para>
/// A row is marked sent only after the sender returned. A crash between the send and the commit sends it again; the stable
/// <c>Message-ID</c> (the delivery id) lets the mail system recognise that.
/// </para>
/// <para>The address and body are personal data: nothing here logs them, and failures are logged by exception type only (an SMTP message may quote the address).</para>
/// </summary>
public sealed partial class EmailDeliveryProcessor(
    IServiceScopeFactory scopeFactory,
    IEmailRenderer renderer,
    IEmailSender sender,
    TimeProvider timeProvider,
    IOptions<EmailDeliveryOptions> options,
    ILogger<EmailDeliveryProcessor> logger)
{
    /// <returns>The number of deliveries claimed in this batch (0 = nothing due).</returns>
    public async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var deliveries = await ClaimBatchAsync(db, ct);
        await SendAllAsync(deliveries, ct);

        // Shutting down mid-batch must still record what was already sent, or those emails go out twice after the restart.
        await db.SaveChangesAsync(CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);
        return deliveries.Count;
    }

    private Task<List<EmailDelivery>> ClaimBatchAsync(AppDbContext db, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        var batchSize = options.Value.BatchSize;

        return db.EmailDeliveries
            .FromSql($"""
                SELECT * FROM email_deliveries
                WHERE status = 'Pending' AND next_attempt_at <= {now}
                ORDER BY next_attempt_at, id
                LIMIT {batchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(ct);
    }

    private async Task SendAllAsync(List<EmailDelivery> deliveries, CancellationToken ct)
    {
        try
        {
            foreach (var delivery in deliveries)
            {
                await SendOneAsync(delivery, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The row being sent when the host stopped stays pending; the ones before it are already marked.
        }
    }

    private async Task SendOneAsync(EmailDelivery delivery, CancellationToken ct)
    {
        try
        {
            var email = renderer.Render(delivery.Template, EmailModelJson.Deserialize(delivery.ModelJson));
            await sender.SendAsync(email.ToMessage(delivery.Id, delivery.ToAddress), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            RecordFailure(delivery, ex);
            return;
        }

        delivery.MarkSent(timeProvider.GetUtcNow());
    }

    private void RecordFailure(EmailDelivery delivery, Exception error)
    {
        var attempts = delivery.Attempts + 1;
        var deadLetter = OutboxRetryPolicy.IsExhausted(attempts);
        var delay = OutboxRetryPolicy.NextDelay(attempts, Random.Shared.NextDouble());
        delivery.RecordFailure(timeProvider.GetUtcNow(), $"{error.GetType().Name}: {error.Message}", delay, deadLetter);

        if (deadLetter)
        {
            LogDeadLettered(logger, delivery.Id, delivery.Template, attempts, error.GetType().Name);
        }
        else
        {
            LogRetryScheduled(logger, delivery.Id, delivery.Template, attempts, delivery.NextAttemptAt, error.GetType().Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Email delivery {DeliveryId} ({Template}) dead-lettered after {Attempts} attempts: {ErrorType}")]
    private static partial void LogDeadLettered(ILogger logger, Guid deliveryId, string template, int attempts, string errorType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Email delivery {DeliveryId} ({Template}) failed attempt {Attempts}; retry at {NextAttemptAt}: {ErrorType}")]
    private static partial void LogRetryScheduled(ILogger logger, Guid deliveryId, string template, int attempts, DateTimeOffset nextAttemptAt, string errorType);
}
