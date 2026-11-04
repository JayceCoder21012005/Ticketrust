using TicketRush.Domain.Primitives;

namespace TicketRush.Domain.Events;

public sealed record EventPublished(Guid EventId, Guid OrganizerId) : IDomainEvent;

public sealed record EventCancelled(Guid EventId, Guid OrganizerId) : IDomainEvent;

public sealed record EventEnded(Guid EventId) : IDomainEvent;

/// <summary>Title, description, lineup or cover of a published event changed: public caches must refresh.</summary>
public sealed record EventDetailsChanged(Guid EventId) : IDomainEvent;
