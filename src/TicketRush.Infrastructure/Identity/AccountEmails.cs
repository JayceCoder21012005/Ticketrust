using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using TicketRush.Application.Abstractions.Messaging;
using TicketRush.Application.Abstractions.Notifications;
using TicketRush.Application.Options;
using TicketRush.Domain.Users;

namespace TicketRush.Infrastructure.Identity;

/// <summary>
/// Tokens are generated at send time (Data Protection keys are shared through Redis), so they never sit
/// in the outbox table. Plain inline copy for now; spec 09 replaces it with Scriban templates.
/// </summary>
internal sealed class SendConfirmationEmailHandler(UserManager<AppUser> users, IEmailSender email, IOptions<FrontendOptions> frontend)
    : IIntegrationEventHandler<UserRegistered>
{
    public async Task HandleAsync(UserRegistered message, IntegrationEventContext context, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(message.UserId.ToString());
        if (user is null || user.EmailConfirmed)
        {
            return;
        }

        var token = await users.GenerateEmailConfirmationTokenAsync(user);
        var link = AccountLinks.Build(frontend.Value, "verify-email", ("userId", user.Id.ToString()), ("token", token));
        await email.SendAsync(AccountLinks.Email(context.MessageId, user, "Xác nhận email TicketRush", "Xác nhận email của bạn", link, "Liên kết có hiệu lực trong 3 ngày."), ct);
    }
}

internal sealed class SendPasswordResetEmailHandler(UserManager<AppUser> users, IEmailSender email, IOptions<FrontendOptions> frontend)
    : IIntegrationEventHandler<PasswordResetRequested>
{
    public async Task HandleAsync(PasswordResetRequested message, IntegrationEventContext context, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(message.UserId.ToString());
        if (user is null)
        {
            return;
        }

        var token = await users.GeneratePasswordResetTokenAsync(user);
        var link = AccountLinks.Build(frontend.Value, "reset-password", ("email", user.Email!), ("token", token));
        await email.SendAsync(AccountLinks.Email(context.MessageId, user, "Đặt lại mật khẩu TicketRush", "Đặt lại mật khẩu", link, "Liên kết có hiệu lực trong 1 giờ. Nếu bạn không yêu cầu, hãy bỏ qua email này."), ct);
    }
}

internal static class AccountLinks
{
    public static string Build(FrontendOptions frontend, string path, params (string Name, string Value)[] query) =>
        $"{frontend.BaseUrl.TrimEnd('/')}/{path}?" + string.Join('&', query.Select(q => $"{q.Name}={Uri.EscapeDataString(q.Value)}"));

    public static EmailMessage Email(Guid messageId, AppUser user, string subject, string action, string link, string note)
    {
        var name = WebUtility.HtmlEncode(user.FullName);
        var href = WebUtility.HtmlEncode(link);
        var html = $"<p>Xin chào {name},</p><p><a href=\"{href}\">{action}</a></p><p>{note}</p>";
        var text = $"Xin chào {user.FullName},\n\n{action}: {link}\n\n{note}";
        return new EmailMessage(messageId, user.Email!, subject, html, text);
    }
}
