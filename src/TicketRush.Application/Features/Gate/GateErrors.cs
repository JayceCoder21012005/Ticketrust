using TicketRush.Domain.Primitives;

namespace TicketRush.Application.Features.Gate;

public static class GateErrors
{
    /// <summary>Unknown event and event of another gate are the same answer, so events cannot be probed.</summary>
    public static readonly Error NotAssigned = Error.NotFound("gate.not_found", "You are not assigned to this event.");

    public static readonly Error StaffNotFound =
        Error.NotFound("gate.staff_not_found", "No gate staff account has this email.");

    public static Error ScanRateLimited(TimeSpan retryAfter) =>
        new(
            "rate_limited",
            "Too many scans. Slow down for a moment.",
            ErrorType.RateLimited,
            Extensions: new Dictionary<string, object?> { [CommonErrors.RetryAfterExtension] = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)) });
}
