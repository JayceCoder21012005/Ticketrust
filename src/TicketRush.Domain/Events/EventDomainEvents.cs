using TicketRush.Domain.Primitives;

namespace TicketRush.Domain.Events;

public sealed record EventPublished(Guid EventId, Guid OrganizerId) : IDomainEvent;

public sealed record EventCancelled(Guid EventId, Guid OrganizerId) : IDomainEvent;

/// <summary>An admin took the event down for moderation (spec 16): tell the organizer why; refresh the catalog.</summary>
public sealed record EventSuspended(Guid EventId, Guid OrganizerId, string Reason) : IDomainEvent;

/// <summary>An admin put a suspended event back on sale (spec 16).</summary>
public sealed record EventRestored(Guid EventId, Guid OrganizerId, string Reason) : IDomainEvent;

public sealed record EventEnded(Guid EventId) : IDomainEvent;

/// <summary>Title, description, lineup or cover of a published event changed: public caches must refresh.</summary>
public sealed record EventDetailsChanged(Guid EventId) : IDomainEvent;
