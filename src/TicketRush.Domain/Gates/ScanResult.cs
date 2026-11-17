namespace TicketRush.Domain.Gates;

/// <summary>What a scan at the gate came to (spec 11). Stored as text in the check-in log.</summary>
public enum ScanResult
{
    /// <summary>Valid ticket, now checked in.</summary>
    Admitted,

    /// <summary>The ticket was admitted by an earlier scan.</summary>
    AlreadyCheckedIn,

    /// <summary>Not a ticket code: bad format, unknown ticket or wrong signature.</summary>
    Invalid,

    /// <summary>A genuine code whose step is outside the ±1 window: an old screenshot.</summary>
    Expired,

    /// <summary>A genuine ticket of another event.</summary>
    WrongEvent,

    /// <summary>A genuine ticket that cannot enter: voided or listed for resale.</summary>
    NotValid,
}
