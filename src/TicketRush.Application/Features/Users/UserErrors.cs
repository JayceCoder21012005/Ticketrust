using TicketRush.Domain.Primitives;

namespace TicketRush.Application.Features.Users;

public static class UserErrors
{
    public static readonly Error NotFound = Error.NotFound("user.not_found", "Account not found.");

    public static readonly Error AvatarNotOwned =
        Error.Validation("user.avatar_invalid", "Upload the avatar through the avatar upload URL first.");
}

public static class AvatarKeys
{
    public static string PrefixFor(Guid userId) => $"avatars/{userId:N}/";
}
