namespace TicketRush.Domain.Primitives;

public abstract class Entity
{
    protected Entity(Guid id) => Id = id;

    // Required by EF Core materialisation.
    protected Entity()
    {
    }

    public Guid Id { get; private init; }
}
