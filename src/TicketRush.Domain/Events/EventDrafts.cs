using TicketRush.Domain.Primitives;

namespace TicketRush.Domain.Events;

/// <summary>One row of <see cref="Event.ReplaceTiers"/>: <see cref="Id"/> set updates that tier, null adds one.</summary>
public sealed record TierDraft(Guid? Id, string Name, Money Price, TicketKind Kind, int? Capacity, string Color);

/// <summary>
/// One row of <see cref="Event.ReplacePhases"/>. <see cref="KeepExistingCode"/> keeps the stored presale hash of the
/// phase with <see cref="Id"/>; otherwise <see cref="AccessCodeHash"/> (null = no code) is used.
/// </summary>
public sealed record PhaseDraft(
    Guid? Id,
    string Name,
    DateTimeOffset OpensAt,
    DateTimeOffset ClosesAt,
    bool QueueEnabled,
    int MaxTicketsPerUser,
    string? AccessCodeHash,
    bool KeepExistingCode);
