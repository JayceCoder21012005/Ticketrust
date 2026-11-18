using FluentValidation;
using TicketRush.Application.Abstractions.Identity;
using TicketRush.Application.Abstractions.Security;
using TicketRush.Application.Abstractions.Storage;
using TicketRush.Domain.Primitives;

namespace TicketRush.Application.Features.Users.UpdateProfile;

/// <summary>Email change is out of scope (spec 03).</summary>
public sealed record UpdateProfileCommand(Guid UserId, string FullName, string? PhoneNumber, string? AvatarUrl);

public sealed class UpdateProfileValidator : AbstractValidator<UpdateProfileCommand>
{
    public UpdateProfileValidator()
    {
        RuleFor(c => c.FullName).NotEmpty().MaximumLength(100).Must(n => !string.IsNullOrWhiteSpace(n));
        RuleFor(c => c.PhoneNumber).Matches(@"^(0|\+84)\d{9}$").When(c => c.PhoneNumber is not null)
            .WithMessage("Phone number must be a Vietnamese mobile number.");
        RuleFor(c => c.AvatarUrl).MaximumLength(2048);
    }
}

public sealed class UpdateProfileHandler(IIdentityService identity, IFileStorage storage, ISuspiciousActivityDetector detector)
{
    public async Task<Result<UserAccount>> HandleAsync(UpdateProfileCommand command, CancellationToken ct)
    {
        // Only accept avatars this user uploaded through their presigned URL, never arbitrary URLs.
        if (command.AvatarUrl is { } url && !storage.IsPublicUrlUnder(url, AvatarKeys.PrefixFor(command.UserId)))
        {
            return UserErrors.AvatarNotOwned;
        }

        var updated = await identity.UpdateProfileAsync(command.UserId, command.FullName.Trim(), command.PhoneNumber, command.AvatarUrl, ct);
        if (updated.IsSuccess)
        {
            // Spec 12: one phone number on many accounts is flagged for review (never blocked).
            await detector.PhoneNumberSavedAsync(command.UserId, command.PhoneNumber, ct);
        }

        return updated;
    }
}
