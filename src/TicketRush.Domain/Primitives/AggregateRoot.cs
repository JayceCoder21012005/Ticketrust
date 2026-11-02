namespace TicketRush.Domain.Primitives;

public abstract class AggregateRoot : Entity, IHasDomainEvents
{
    private readonly List<IDomainEvent> _domainEvents = [];

    protected AggregateRoot(Guid id)
        : base(id)
    {
    }

    protected AggregateRoot()
    {
    }

    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    public IReadOnlyList<IDomainEvent> DequeueDomainEvents()
    {
        var events = _domainEvents.ToList();
        _domainEvents.Clear();
        return events;
    }

    protected void Raise(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);
}

public interface IHasDomainEvents
{
    Guid Id { get; }

    IReadOnlyCollection<IDomainEvent> DomainEvents { get; }

    IReadOnlyList<IDomainEvent> DequeueDomainEvents();
}

/// <summary>Timestamps are stamped by the persistence layer, never by domain code.</summary>
public interface IAuditable
{
    DateTimeOffset CreatedAt { get; }

    DateTimeOffset UpdatedAt { get; }
}
