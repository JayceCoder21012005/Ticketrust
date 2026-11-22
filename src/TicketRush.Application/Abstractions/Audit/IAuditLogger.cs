namespace TicketRush.Application.Abstractions.Audit;

/// <summary>Who is acting right now: the signed-in user, their IP and the request's correlation id. All null for the system.</summary>
public interface IAuditContext
{
    Guid? ActorUserId { get; }

    string? ActorIp { get; }

    string? CorrelationId { get; }
}

/// <summary>
/// Explicit audit entries (spec 16) for what the interceptor cannot see: security events and admin actions with their reason.
/// <see cref="Record"/> adds the entry to the current unit of work, so it commits (or not) with the change it describes;
/// <see cref="LogAsync"/> also saves, for events that change nothing else (a failed login).
/// </summary>
public interface IAuditLogger
{
    void Record(string action, string entityType, string? entityId, object? details = null);

    Task LogAsync(string action, string entityType, string? entityId, object? details, CancellationToken ct);
}
