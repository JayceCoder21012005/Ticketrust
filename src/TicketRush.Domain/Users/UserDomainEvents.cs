using TicketRush.Domain.Primitives;

namespace TicketRush.Domain.Users;

/// <summary>Triggers the confirmation email. The token is generated when the email is sent, never stored.</summary>
public sealed record UserRegistered(Guid UserId) : IDomainEvent;

/// <summary>Triggers the reset-password email (link valid for 1 hour, spec 09).</summary>
public sealed record PasswordResetRequested(Guid UserId) : IDomainEvent;
