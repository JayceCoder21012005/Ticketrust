namespace TicketRush.Application.Abstractions.Queue;

/// <summary>Per-user operations on a sale phase's waiting room. All state lives in Redis (spec 06).</summary>
public interface IWaitingRoom
{
    /// <summary>Idempotent: a user already in the line gets the same number back.</summary>
    Task<JoinResult> JoinAsync(SaleWindow sale, Guid userId, CancellationToken ct);

    /// <summary>
    /// Reads the user's state and, when they are due and capacity allows, admits them. Concurrent calls
    /// (several tabs) end up with the same admission token.
    /// </summary>
    Task<ClaimResult> StatusAsync(SaleWindow sale, Guid userId, CancellationToken ct);

    /// <summary>
    /// True when the user already holds a place in this sale (pre-queue or line) and has not left or expired. A returning
    /// member does not have to present the presale code again (reloads, second tab).
    /// </summary>
    Task<bool> IsMemberAsync(SaleWindow sale, Guid userId, CancellationToken ct);

    /// <summary>
    /// True when the user was in this sale and is now done: they left, or their shopping window ended. Lets the status
    /// of a finished room still be shown while another phase's pre-queue is running.
    /// </summary>
    Task<bool> IsDoneAsync(SaleWindow sale, Guid userId, CancellationToken ct);

    /// <summary>Removes the user from the line and from the active set; their slot is free immediately.</summary>
    Task LeaveAsync(SaleWindow sale, Guid userId, CancellationToken ct);

    /// <summary>True while the user's admission has not expired. Used to revoke tokens on leave or expiry.</summary>
    Task<bool> IsActiveAsync(Guid phaseId, Guid userId, CancellationToken ct);

    /// <summary>
    /// True when <paramref name="token"/> is the user's current admission token and the admission has not expired.
    /// A token from an earlier admission (the user left and came back) is not current.
    /// </summary>
    Task<bool> IsCurrentTokenAsync(Guid phaseId, Guid userId, string token, CancellationToken ct);

    /// <summary>Admissions per second actually observed (EMA over about 60 s); 0 when unknown.</summary>
    Task<double> GetRateEmaAsync(Guid phaseId, CancellationToken ct);
}
