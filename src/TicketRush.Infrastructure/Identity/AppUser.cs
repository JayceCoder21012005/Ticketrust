using Microsoft.AspNetCore.Identity;
using TicketRush.Domain.Primitives;

namespace TicketRush.Infrastructure.Identity;

/// <summary>Identity user. Lives in Infrastructure because Identity types must not leak into Domain.</summary>
public sealed class AppUser : IdentityUser<Guid>, IAuditable
{
    public string FullName { get; set; } = string.Empty;

    public string? AvatarUrl { get; set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }
}

public sealed class AppRole : IdentityRole<Guid>
{
    public AppRole()
    {
    }

    public AppRole(string roleName)
        : base(roleName)
    {
        Id = Guid.CreateVersion7();
        NormalizedName = roleName.ToUpperInvariant();
    }
}
