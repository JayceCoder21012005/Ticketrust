namespace TicketRush.Application.Abstractions.Gates;

/// <param name="Allowed">False once the staff member used up the scans of the current second.</param>
/// <param name="RetryAfter">When refused: how long until the window resets.</param>
/// <param name="Previous">
/// The stored answer to the same payload scanned by the same staff member within the last 3 seconds (a camera reading one code
/// twice), or null.
/// </param>
public sealed record ScanGate(bool Allowed, TimeSpan RetryAfter, string? Previous);

/// <summary>
/// The two cheap guards of the scan endpoint (spec 11), both kept in Redis so every api instance agrees: 5 scans per second per
/// staff member, and the 3-second memory of the last answer per (staff, payload). The payload is only ever hashed.
/// </summary>
public interface IScanGuard
{
    public const int ScansPerSecond = 5;

    public static readonly TimeSpan RepeatWindow = TimeSpan.FromSeconds(3);

    /// <summary>Counts one scan and returns the remembered answer of this payload, if any.</summary>
    /// <exception cref="Tickets.ThrottleUnavailableException">Redis did not answer: callers fail closed.</exception>
    Task<ScanGate> EnterAsync(Guid staffUserId, string payload, CancellationToken ct);

    /// <summary>Keeps <paramref name="answerJson"/> for <see cref="RepeatWindow"/>. Best effort: a failure only loses the double-read shield.</summary>
    Task RememberAsync(Guid staffUserId, string payload, string answerJson, CancellationToken ct);
}
