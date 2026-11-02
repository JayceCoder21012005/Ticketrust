namespace TicketRush.Domain.Events;

public enum EventStatus
{
    Draft,
    Published,
    Ended,
    Cancelled,
}

public enum TicketKind
{
    Seated,
    GeneralAdmission,
}
