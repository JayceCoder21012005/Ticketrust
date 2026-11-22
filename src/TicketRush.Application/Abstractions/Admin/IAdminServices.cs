using TicketRush.Domain.Primitives;

namespace TicketRush.Application.Abstractions.Admin;

public sealed record AdminUser(
    Guid Id,
    string Email,
    string FullName,
    string? PhoneNumber,
    bool EmailConfirmed,
    bool Locked,
    DateTimeOffset? LockoutEnd,
    IReadOnlyList<string> Roles,
    DateTimeOffset CreatedAt);

public sealed record AdminPage<T>(IReadOnlyList<T> Items, string? NextCursor);

/// <summary>The Identity side of the admin area (spec 16). Every change saves at once, with whatever the caller recorded.</summary>
public interface IAdminUsers
{
    /// <param name="query">Part of an email or a name; null lists everyone, newest first.</param>
    Task<AdminPage<AdminUser>> SearchAsync(string? query, string? cursor, int limit, CancellationToken ct);

    Task<AdminUser?> GetAsync(Guid userId, CancellationToken ct);

    /// <summary>Locks the account indefinitely and ends its sessions (every refresh token family).</summary>
    Task<Result> LockAsync(Guid userId, CancellationToken ct);

    Task<Result> UnlockAsync(Guid userId, CancellationToken ct);

    Task<Result> GrantRoleAsync(Guid userId, string role, CancellationToken ct);

    Task<Result> RevokeRoleAsync(Guid userId, string role, CancellationToken ct);
}

public sealed record DeadLetter(Guid Id, string Type, Guid? AggregateId, DateTimeOffset OccurredAt, int Attempts, string Error, string Payload);

/// <summary>The outbox's dead letters (spec 16).</summary>
public interface IAdminOutbox
{
    Task<AdminPage<DeadLetter>> ListAsync(string? cursor, int limit, CancellationToken ct);

    /// <returns>False when there is no such dead letter (unknown, retried or discarded already).</returns>
    Task<bool> RetryAsync(Guid messageId, Action record, CancellationToken ct);

    Task<bool> DiscardAsync(Guid messageId, string reason, Action record, CancellationToken ct);
}
