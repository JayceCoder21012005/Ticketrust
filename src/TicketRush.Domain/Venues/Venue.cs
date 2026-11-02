using TicketRush.Domain.Primitives;

namespace TicketRush.Domain.Venues;

public sealed class Venue : AggregateRoot, IAuditable
{
    private Venue(Guid id, string name, string address, string city, string? mapUrl, int capacity)
        : base(id)
    {
        Name = name;
        Address = address;
        City = city;
        MapUrl = mapUrl;
        Capacity = capacity;
    }

    private Venue()
    {
    }

    public string Name { get; private set; } = null!;

    public string Address { get; private set; } = null!;

    public string City { get; private set; } = null!;

    public string? MapUrl { get; private set; }

    public int Capacity { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static Venue Create(string name, string address, string city, string? mapUrl, int capacity, Guid? id = null)
    {
        if (capacity <= 0)
        {
            throw new DomainException("venue.invalid_capacity", "Venue capacity must be positive.");
        }

        return new Venue(id ?? Guid.CreateVersion7(), name.Trim(), address.Trim(), city.Trim(), mapUrl, capacity);
    }
}
