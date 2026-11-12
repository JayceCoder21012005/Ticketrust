using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using TicketRush.Application.Abstractions.Messaging;
using TicketRush.Application.Abstractions.Notifications;
using TicketRush.Application.Options;
using TicketRush.Domain.Users;

namespace TicketRush.Infrastructure.Identity;

/// <summary>
/// Tokens are generated at send time (Data Protection keys are shared through Redis), so they never sit
/// in the outbox table. The copy lives in the <c>confirm-email</c> / <c>reset-password</c> templates.
/// </summary>
internal sealed class SendConfirmationEmailHandler(UserManager<AppUser> users, IEmailSender email, IEmailRenderer renderer, IOptions<FrontendOptions> frontend)
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
        var mail = renderer.Render("confirm-email", new { name = user.FullName, link });
        await email.SendAsync(mail.ToMessage(context.MessageId, user.Email!), ct);
    }
}

internal sealed class SendPasswordResetEmailHandler(UserManager<AppUser> users, IEmailSender email, IEmailRenderer renderer, IOptions<FrontendOptions> frontend)
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
        var mail = renderer.Render("reset-password", new { name = user.FullName, link });
        await email.SendAsync(mail.ToMessage(context.MessageId, user.Email!), ct);
    }
}

internal static class AccountLinks
{
    public static string Build(FrontendOptions frontend, string path, params (string Name, string Value)[] query) =>
        $"{frontend.BaseUrl.TrimEnd('/')}/{path}?" + string.Join('&', query.Select(q => $"{q.Name}={Uri.EscapeDataString(q.Value)}"));
}
