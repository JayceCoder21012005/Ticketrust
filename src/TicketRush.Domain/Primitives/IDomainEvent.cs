namespace TicketRush.Domain.Primitives;

/// <summary>
/// Something meaningful that happened inside an aggregate. Domain events are persisted to the
/// transactional outbox in the same transaction as the aggregate change (C8), so they must be
/// plain, JSON-serialisable records.
/// </summary>
#pragma warning disable CA1040 // Marker interface is intentional: it drives outbox type discovery.
public interface IDomainEvent;
#pragma warning restore CA1040
