using FluentValidation;
using TicketRush.Application.Abstractions.Identity;
using TicketRush.Domain.Primitives;

namespace TicketRush.Application.Features.Auth.Register;

public sealed record RegisterCommand(string Email, string Password, string FullName);

public sealed class RegisterValidator : AbstractValidator<RegisterCommand>
{
    public RegisterValidator()
    {
        RuleFor(c => c.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(c => c.FullName).NotEmpty().MaximumLength(100).Must(n => !string.IsNullOrWhiteSpace(n));
        RuleFor(c => c.Password).StrongPassword(c => c.Email);
    }
}

public sealed class RegisterHandler(IIdentityService identity)
{
    public Task<Result> HandleAsync(RegisterCommand command, CancellationToken ct) =>
        identity.RegisterAsync(command.Email.Trim(), command.Password, command.FullName.Trim(), ct);
}
