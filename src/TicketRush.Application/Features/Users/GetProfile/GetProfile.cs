using TicketRush.Application.Abstractions.Identity;
using TicketRush.Domain.Primitives;

namespace TicketRush.Application.Features.Users.GetProfile;

public sealed class GetProfileHandler(IIdentityService identity)
{
    public async Task<Result<UserAccount>> HandleAsync(Guid userId, CancellationToken ct) =>
        await identity.FindByIdAsync(userId, ct) is { } user ? user : UserErrors.NotFound;
}
