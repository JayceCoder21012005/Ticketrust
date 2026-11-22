using System.Diagnostics;

namespace TicketRush.Infrastructure.Observability;

/// <summary>
/// Carries a trace across the hops that are not HTTP calls (spec 17): the outbox, the email queue and the mock gateway's
/// webhook queue store the W3C <c>traceparent</c> of the work that wrote them, and whoever picks them up later continues it.
/// </summary>
public static class TraceContext
{
    /// <summary>W3C traceparent: <c>00-{32 hex}-{16 hex}-{2 hex}</c>.</summary>
    public const int MaxLength = 55;

    /// <summary>The current span as a traceparent, or <c>null</c> outside any span (a sweep that runs on its own).</summary>
    public static string? CurrentParent() =>
        Activity.Current is { IdFormat: ActivityIdFormat.W3C, Id: { Length: <= MaxLength } id } ? id : null;

    /// <summary>The stored parent, or <c>default</c> (a new root) when there is none or it does not parse.</summary>
    public static ActivityContext ParentOf(string? traceParent) =>
        traceParent is not null && ActivityContext.TryParse(traceParent, null, isRemote: true, out var context) ? context : default;
}
