namespace TicketRush.Application.Abstractions.Notifications;

/// <param name="MessageId">Idempotency key: the outbox message id, so a retried send can be recognised.</param>
public sealed record EmailMessage(Guid MessageId, string To, string Subject, string HtmlBody, string TextBody);

/// <summary>Outgoing email. Spec 09 adds templates and notification types on top of this.</summary>
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken ct);
}
