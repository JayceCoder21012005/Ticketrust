using TicketRush.Domain.Primitives;

namespace TicketRush.Domain.Events;

/// <summary>A price category of an event. Owned by <see cref="Event"/>; only created through it.</summary>
[TicketRush.Domain.Audit.Audited]
public sealed class TicketTier : Entity
{
    internal TicketTier(Guid eventId, string name, Money price, TicketKind kind, int? capacity, string color)
        : base(Guid.CreateVersion7())
    {
        EventId = eventId;
        Update(name, price, kind, capacity, color);
    }

    private TicketTier()
    {
    }

    public Guid EventId { get; private set; }

    public string Name { get; private set; } = null!;

    public Money Price { get; private set; } = null!;

    public TicketKind Kind { get; private set; }

    public int? Capacity { get; private set; }

    public string Color { get; private set; } = null!;

    internal void Update(string name, Money price, TicketKind kind, int? capacity, string color)
    {
        if (price.Amount <= 0)
        {
            throw new DomainException("tier.invalid_price", "Tier price must be greater than zero.");
        }

        if (kind == TicketKind.GeneralAdmission && capacity is not > 0)
        {
            throw new DomainException("tier.invalid_capacity", "General admission tiers need a positive capacity.");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("tier.invalid_name", "Tier name is required.");
        }

        Name = name.Trim();
        Price = price;
        Kind = kind;
        // Seated capacity is derived from the seats assigned to the tier (spec 05), never stored.
        Capacity = kind == TicketKind.GeneralAdmission ? capacity : null;
        Color = color;
    }
}
