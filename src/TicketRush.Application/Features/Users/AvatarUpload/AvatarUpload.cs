using FluentValidation;
using TicketRush.Application.Abstractions.Storage;

namespace TicketRush.Application.Features.Users.AvatarUpload;

public sealed record AvatarUploadCommand(Guid UserId, string ContentType, long Size);

public sealed class AvatarUploadValidator : AbstractValidator<AvatarUploadCommand>
{
    public const long MaxBytes = 2 * 1024 * 1024;

    // Raster formats only: SVG can carry script.
    public static readonly IReadOnlyDictionary<string, string> Extensions = new Dictionary<string, string>
    {
        ["image/jpeg"] = "jpg",
        ["image/png"] = "png",
        ["image/webp"] = "webp",
        ["image/avif"] = "avif",
    };

    public AvatarUploadValidator()
    {
        RuleFor(c => c.ContentType).Must(Extensions.ContainsKey).WithMessage("Use a JPEG, PNG, WebP or AVIF image.");
        RuleFor(c => c.Size).InclusiveBetween(1, MaxBytes).WithMessage("The image must be 2 MB or smaller.");
    }
}

public sealed class AvatarUploadHandler(IFileStorage storage)
{
    public UploadTicket Handle(AvatarUploadCommand command)
    {
        var extension = AvatarUploadValidator.Extensions[command.ContentType];
        var key = $"{AvatarKeys.PrefixFor(command.UserId)}{Guid.CreateVersion7():N}.{extension}";
        return storage.CreateUploadUrl(key, command.ContentType, command.Size);
    }
}
