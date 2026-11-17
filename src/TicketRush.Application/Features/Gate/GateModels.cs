using TicketRush.Domain.Gates;

namespace TicketRush.Application.Features.Gate;

/// <summary>Seat and holder shown to the gate after a scan. Seat parts are null for general admission.</summary>
public sealed record ScanTicketInfo(string Tier, string? Section, string? Row, string? Seat, string HolderName);

/// <param name="Reason">A short machine code for a refusal (<c>bad_format</c>, <c>bad_signature</c>, <c>unknown_ticket</c>, <c>voided</c>, <c>listed</c>); null when admitted.</param>
/// <param name="CheckedInAt">The time of the admission: now for <c>Admitted</c>, the first scan's time for <c>AlreadyCheckedIn</c>.</param>
/// <param name="GateName">For <c>AlreadyCheckedIn</c>: the gate of the first scan.</param>
public sealed record ScanResponse(ScanResult Result, string? Reason, ScanTicketInfo? Ticket, DateTimeOffset? CheckedInAt, string? GateName);

public sealed record GateEventItem(
    Guid EventId,
    string Slug,
    string Title,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string VenueName,
    string City,
    string GateName);

public sealed record GateStatsResponse(int CheckedIn, int Total, int LastMinute);
