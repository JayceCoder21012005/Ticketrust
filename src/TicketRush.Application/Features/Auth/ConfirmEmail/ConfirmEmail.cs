using FluentValidation;
using TicketRush.Application.Abstractions.Identity;
using TicketRush.Domain.Primitives;

namespace TicketRush.Application.Features.Auth.ConfirmEmail;

public sealed record ConfirmEmailCommand(Guid UserId, string Token);

public sealed class ConfirmEmailValidator : AbstractValidator<ConfirmEmailCommand>
{
    public ConfirmEmailValidator()
    {
        RuleFor(c => c.UserId).NotEmpty();
        RuleFor(c => c.Token).NotEmpty().MaximumLength(2048);
    }
}

public sealed class ConfirmEmailHandler(IIdentityService identity)
{
    public Task<Result> HandleAsync(ConfirmEmailCommand command, CancellationToken ct) =>
        identity.ConfirmEmailAsync(command.UserId, command.Token, ct);
}
