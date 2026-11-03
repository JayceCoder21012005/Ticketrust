using System.Data.Common;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using MimeKit;
using TicketRush.Application.Abstractions.Notifications;
using TicketRush.Infrastructure.Options;

namespace TicketRush.Infrastructure.Email;

/// <summary>MailKit SMTP. The endpoint comes from <c>ConnectionStrings:mailpit</c> (<c>Endpoint=smtp://host:port</c>).</summary>
internal sealed class SmtpEmailSender(IConfiguration configuration, IOptions<EmailOptions> options) : IEmailSender
{
    public const string ConnectionName = "mailpit";

    public async Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        var endpoint = Endpoint();
        var email = new MimeMessage
        {
            MessageId = $"{message.MessageId:N}@ticketrush",
            Subject = message.Subject,
            Body = new BodyBuilder { HtmlBody = message.HtmlBody, TextBody = message.TextBody }.ToMessageBody(),
        };
        email.From.Add(new MailboxAddress(options.Value.FromName, options.Value.FromAddress));
        email.To.Add(MailboxAddress.Parse(message.To));

        using var client = new SmtpClient();
        await client.ConnectAsync(endpoint.Host, endpoint.Port, SecureSocketOptions.Auto, ct);
        await client.SendAsync(email, ct);
        await client.DisconnectAsync(quit: true, ct);
    }

    private Uri Endpoint()
    {
        var connectionString = configuration.GetConnectionString(ConnectionName)
            ?? throw new InvalidOperationException($"ConnectionStrings:{ConnectionName} is not configured.");
        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
        return new Uri((string)builder["Endpoint"]);
    }
}
