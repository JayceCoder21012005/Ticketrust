namespace TicketRush.Domain.Events;

public enum EventStatus
{
    Draft,
    Published,
    Ended,
    Cancelled,

    /// <summary>
    /// Taken down by an admin for moderation (spec 16): hidden from the catalog and closed to holds, orders, resale and transfers,
    /// while sold tickets stay valid (QR shown, gate admits). The admin can restore it to <see cref="Published"/>.
    /// </summary>
    Suspended,
}

public enum TicketKind
{
    Seated,
    GeneralAdmission,
}
