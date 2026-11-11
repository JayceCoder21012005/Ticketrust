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
/// Sends the due <see cref="EmailDeliveryStatus.Pending"/> deliveries, one per transaction: claim a row with
/// <c>FOR UPDATE SKIP LOCKED</c> (several worker instances never take the same row), send it, record the outcome, commit.
/// A failing address only reschedules itself (<see cref="OutboxRetryPolicy"/> backoff, parked after
/// <see cref="OutboxRetryPolicy.MaxAttempts"/>) and the rest still go out. Every send is bounded by
/// <see cref="EmailDeliveryOptions.SendTimeout"/>, so a dropped SMTP connection cannot keep a row lock open for long.
/// <para>
/// A row is marked sent only after the sender returned, and is committed on its own, so nothing already sent is ever
/// rolled back by a later row. The remaining window: a crash between the SMTP server accepting the mail and that one row's
/// commit sends that one email again after the restart. The delivery id is the <c>Message-ID</c>, which only helps receivers
/// that deduplicate on it.
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
    /// <returns>The number of deliveries handled in this call, at most <see cref="EmailDeliveryOptions.BatchSize"/> (0 = nothing due).</returns>
    public async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        var handled = 0;
        try
        {
            while (handled < options.Value.BatchSize && await ProcessNextAsync(ct))
            {
                handled++;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The row being sent when the host stopped was never committed and stays pending; earlier rows are already committed.
        }

        return handled;
    }

    private async Task<bool> ProcessNextAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var delivery = await ClaimNextAsync(db, ct);
        if (delivery is null)
        {
            return false;
        }

        await SendOneAsync(delivery, ct);
        await db.SaveChangesAsync(CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);
        return true;
    }

    private Task<EmailDelivery?> ClaimNextAsync(AppDbContext db, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();

        return db.EmailDeliveries
            .FromSql($"""
                SELECT * FROM email_deliveries
                WHERE status = 'Pending' AND next_attempt_at <= {now}
                ORDER BY next_attempt_at, id
                LIMIT 1
                FOR UPDATE SKIP LOCKED
                """)
            .SingleOrDefaultAsync(ct);
    }

    private async Task SendOneAsync(EmailDelivery delivery, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(options.Value.SendTimeout);
        try
        {
            var email = renderer.Render(delivery.Template, EmailModelJson.Deserialize(delivery.ModelJson));
            await sender.SendAsync(email.ToMessage(delivery.Id, delivery.ToAddress), limit.Token);
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            RecordFailure(delivery, new TimeoutException($"The send did not finish within {options.Value.SendTimeout}."));
            return;
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
