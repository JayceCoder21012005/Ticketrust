using Microsoft.AspNetCore.Identity;
using TicketRush.Domain.Primitives;
using TicketRush.Domain.Users;

namespace TicketRush.Infrastructure.Identity;

/// <summary>
/// Identity user. Lives in Infrastructure because Identity types must not leak into Domain.
/// Raises domain events so account emails go through the outbox in the same transaction (C8).
/// </summary>
[TicketRush.Domain.Audit.Audited(nameof(LockoutEnd), Creates = false)]
public sealed class AppUser : IdentityUser<Guid>, IAuditable, IHasDomainEvents
{
    private readonly List<IDomainEvent> _domainEvents = [];

    public string FullName { get; set; } = string.Empty;

    public string? AvatarUrl { get; set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    public IReadOnlyList<IDomainEvent> DequeueDomainEvents()
    {
        var events = _domainEvents.ToList();
        _domainEvents.Clear();
        return events;
    }

    public void MarkRegistered() => _domainEvents.Add(new UserRegistered(Id));

    public void RequestPasswordReset() => _domainEvents.Add(new PasswordResetRequested(Id));
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
