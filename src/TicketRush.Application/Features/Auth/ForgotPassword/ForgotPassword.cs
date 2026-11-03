using FluentValidation;
using TicketRush.Application.Abstractions.Identity;

namespace TicketRush.Application.Features.Auth.ForgotPassword;

public sealed record ForgotPasswordCommand(string Email);

public sealed class ForgotPasswordValidator : AbstractValidator<ForgotPasswordCommand>
{
    public ForgotPasswordValidator()
    {
        RuleFor(c => c.Email).NotEmpty().EmailAddress().MaximumLength(256);
    }
}

/// <summary>No result: the endpoint answers 202 whether or not the email exists (no enumeration).</summary>
public sealed class ForgotPasswordHandler(IIdentityService identity)
{
    public Task HandleAsync(ForgotPasswordCommand command, CancellationToken ct) =>
        identity.RequestPasswordResetAsync(command.Email.Trim(), ct);
}
