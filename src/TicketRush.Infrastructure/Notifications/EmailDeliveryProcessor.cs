using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TicketRush.Application.Abstractions.Notifications;
using TicketRush.Domain.Notifications;
using TicketRush.Infrastructure.Options;
using TicketRush.Infrastructure.Persistence;

namespace TicketRush.Infrastructure.Notifications;

/// <param name="Handled">Deliveries handled in the pass (sent, failed, dead-lettered or expired); 0 = nothing was due.</param>
/// <param name="OutageSuspected">The pass stopped early because SMTP looks down (see <see cref="EmailDeliveryOptions.OutageFailureThreshold"/>).</param>
public sealed record EmailBatchOutcome(int Handled, bool OutageSuspected);

/// <summary>
/// Sends the due <see cref="EmailDeliveryStatus.Pending"/> deliveries, one per transaction: claim a row with
/// <c>FOR UPDATE SKIP LOCKED</c> (several worker instances never take the same row), send it, record the outcome, commit.
/// A failing address only reschedules itself (<see cref="EmailRetryPolicy"/>, about a day of retries) and the rest still go out;
/// a delivery that can never succeed (it cannot render, or the server refused it for good) is parked at once, and one whose
/// moment has passed (<see cref="EmailDelivery.ExpiresAt"/>) is parked unsent. Every send is bounded by
/// <see cref="EmailDeliveryOptions.SendTimeout"/>, so a dropped SMTP connection cannot keep a row lock open for long.
/// <para>
/// Outage breaker: after <see cref="EmailDeliveryOptions.OutageFailureThreshold"/> consecutive transport failures the pass ends,
/// so a global outage charges a few rows instead of the retry budget of the whole backlog; the rows not tried are not charged.
/// The count survives between passes until a send succeeds, so while SMTP stays down each later pass is a one-row probe.
/// </para>
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
    private enum RowResult
    {
        NothingDue,
        Handled,
        TransportFailure,
    }

    private int _consecutiveTransportFailures;

    /// <returns>The number of deliveries handled in this call, at most <see cref="EmailDeliveryOptions.BatchSize"/> (0 = nothing due).</returns>
    public async Task<int> ProcessBatchAsync(CancellationToken ct) => (await RunBatchAsync(ct)).Handled;

    /// <summary>One pass: up to <see cref="EmailDeliveryOptions.BatchSize"/> deliveries, fewer when nothing more is due or SMTP looks down.</summary>
    public async Task<EmailBatchOutcome> RunBatchAsync(CancellationToken ct)
    {
        var handled = 0;
        try
        {
            while (handled < options.Value.BatchSize)
            {
                var result = await ProcessNextAsync(ct);
                if (result == RowResult.NothingDue)
                {
                    break;
                }

                handled++;
                if (result == RowResult.TransportFailure && Volatile.Read(ref _consecutiveTransportFailures) >= options.Value.OutageFailureThreshold)
                {
                    LogOutageSuspected(logger, Volatile.Read(ref _consecutiveTransportFailures));
                    return new EmailBatchOutcome(handled, OutageSuspected: true);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The row being sent when the host stopped was never committed and stays pending; earlier rows are already committed.
        }

        return new EmailBatchOutcome(handled, OutageSuspected: false);
    }

    private async Task<RowResult> ProcessNextAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var delivery = await ClaimNextAsync(db, ct);
        if (delivery is null)
        {
            return RowResult.NothingDue;
        }

        var result = await HandleOneAsync(delivery, ct);
        await db.SaveChangesAsync(CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);
        return result;
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

    private async Task<RowResult> HandleOneAsync(EmailDelivery delivery, CancellationToken ct)
    {
        if (delivery.IsExpired(timeProvider.GetUtcNow()))
        {
            // A reminder of a moment that has passed (the worker was behind, or SMTP was down): sending it now would be wrong.
            delivery.MarkExpired();
            LogExpired(logger, delivery.Id, delivery.Template);
            return RowResult.Handled;
        }

        RenderedEmail email;
        try
        {
            email = renderer.Render(delivery.Template, EmailModelJson.Deserialize(delivery.ModelJson));
        }
        catch (Exception ex)
        {
            RecordFailure(delivery, ex, EmailFailures.OfRender(ex));
            return RowResult.Handled;
        }

        return await SendOneAsync(delivery, email, ct);
    }

    private async Task<RowResult> SendOneAsync(EmailDelivery delivery, RenderedEmail email, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(options.Value.SendTimeout);
        try
        {
            await sender.SendAsync(email.ToMessage(delivery.Id, delivery.ToAddress), limit.Token);
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return RecordSendFailure(delivery, new TimeoutException($"The send did not finish within {options.Value.SendTimeout}."));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return RecordSendFailure(delivery, ex);
        }

        Interlocked.Exchange(ref _consecutiveTransportFailures, 0);
        delivery.MarkSent(timeProvider.GetUtcNow());
        return RowResult.Handled;
    }

    private RowResult RecordSendFailure(EmailDelivery delivery, Exception error)
    {
        var kind = EmailFailures.OfSend(error);
        RecordFailure(delivery, error, kind);
        if (kind == EmailFailureKind.Transport)
        {
            Interlocked.Increment(ref _consecutiveTransportFailures);
            return RowResult.TransportFailure;
        }

        if (kind == EmailFailureKind.Permanent)
        {
            Interlocked.Exchange(ref _consecutiveTransportFailures, 0); // the server answered: it is up
        }

        return RowResult.Handled;
    }

    private void RecordFailure(EmailDelivery delivery, Exception error, EmailFailureKind kind)
    {
        var policy = EmailRetryPolicy.From(options.Value);
        var attempts = delivery.Attempts + 1;
        var deadLetter = kind == EmailFailureKind.Permanent || policy.IsExhausted(attempts);
        var delay = policy.NextDelay(attempts, Random.Shared.NextDouble());
        delivery.RecordFailure(timeProvider.GetUtcNow(), $"{error.GetType().Name}: {error.Message}", delay, deadLetter);

        if (kind == EmailFailureKind.Permanent)
        {
            LogPermanentFailure(logger, delivery.Id, delivery.Template, error.GetType().Name);
        }
        else if (deadLetter)
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

    [LoggerMessage(Level = LogLevel.Error, Message = "Email delivery {DeliveryId} ({Template}) dead-lettered at once, retrying cannot help: {ErrorType}")]
    private static partial void LogPermanentFailure(ILogger logger, Guid deliveryId, string template, string errorType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Email delivery {DeliveryId} ({Template}) failed attempt {Attempts}; retry at {NextAttemptAt}: {ErrorType}")]
    private static partial void LogRetryScheduled(ILogger logger, Guid deliveryId, string template, int attempts, DateTimeOffset nextAttemptAt, string errorType);

    [LoggerMessage(Level = LogLevel.Information, Message = "Email delivery {DeliveryId} ({Template}) expired before it could be sent; parked unsent")]
    private static partial void LogExpired(ILogger logger, Guid deliveryId, string template);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SMTP looks down ({Failures} transport failures in a row); pausing email delivery")]
    private static partial void LogOutageSuspected(ILogger logger, int failures);
}
