using TicketRush.Domain.Primitives;

namespace TicketRush.Domain.Gates;

/// <summary>
/// The organizer assigns a <c>GateStaff</c> user to an event and names the gate they work at (spec 11). One assignment per
/// (event, user): assigning again only renames the gate. The inherited <c>Id</c> is not stored; the pair is the key.
/// </summary>
public sealed class GateAssignment : Entity
{
    public const int GateNameMaxLength = 60;

    private GateAssignment(Guid eventId, Guid userId, string gateName, DateTimeOffset now)
        : base(Guid.CreateVersion7())
    {
        EventId = eventId;
        UserId = userId;
        GateName = gateName;
        CreatedAt = now;
    }

    // Required by EF Core.
    private GateAssignment()
    {
    }

    public Guid EventId { get; private set; }

    public Guid UserId { get; private set; }

    public string GateName { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public static GateAssignment Create(Guid eventId, Guid userId, string gateName, DateTimeOffset now)
    {
        if (eventId == Guid.Empty || userId == Guid.Empty)
        {
            throw new DomainException("gate.invalid_reference", "A gate assignment needs an event and a user.");
        }

        return new GateAssignment(eventId, userId, NormalizeName(gateName), now);
    }

    public void Rename(string gateName) => GateName = NormalizeName(gateName);

    private static string NormalizeName(string gateName)
    {
        var name = gateName?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > GateNameMaxLength)
        {
            throw new DomainException("gate.invalid_name", $"A gate name is 1 to {GateNameMaxLength} characters.");
        }

        return name;
    }
}
