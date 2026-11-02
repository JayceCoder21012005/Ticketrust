using TicketRush.Domain.Primitives;

namespace TicketRush.Domain.Events;

public sealed record EventPublished(Guid EventId, Guid OrganizerId) : IDomainEvent;

public sealed record EventCancelled(Guid EventId, Guid OrganizerId) : IDomainEvent;

public sealed record EventEnded(Guid EventId) : IDomainEvent;
