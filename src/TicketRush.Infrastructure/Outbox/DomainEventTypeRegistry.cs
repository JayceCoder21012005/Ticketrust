using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;
using TicketRush.Application.Abstractions.Messaging;
using TicketRush.Domain.Primitives;

namespace TicketRush.Infrastructure.Outbox;

/// <summary>
/// Maps the stable <c>outbox_messages.type</c> name to a CLR type. The name is the type's simple name,
/// so moving an event to another namespace doesn't orphan messages already in the table.
/// </summary>
public sealed class DomainEventTypeRegistry
{
    public static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly FrozenDictionary<string, Type> _types;

    public DomainEventTypeRegistry(IEnumerable<IntegrationEventHandlerRegistration> registrations)
    {
        var types = typeof(IDomainEvent).Assembly.GetTypes()
            .Where(type => type is { IsAbstract: false, IsInterface: false } && type.IsAssignableTo(typeof(IDomainEvent)))
            .Concat(registrations.Select(registration => registration.EventType))
            .Distinct();

        _types = types.ToFrozenDictionary(NameOf, StringComparer.Ordinal);
    }

    public static string NameOf(Type eventType) => eventType.Name;

    public bool TryResolve(string name, out Type type) => _types.TryGetValue(name, out type!);

    public static string Serialize(IDomainEvent domainEvent) =>
        JsonSerializer.Serialize(domainEvent, domainEvent.GetType(), SerializerOptions);

    public object Deserialize(OutboxMessage message)
    {
        if (!TryResolve(message.Type, out var type))
        {
            throw new InvalidOperationException($"Unknown outbox message type '{message.Type}'.");
        }

        return JsonSerializer.Deserialize(message.Payload, type, SerializerOptions)
            ?? throw new InvalidOperationException($"Outbox message {message.Id} has an empty payload.");
    }
}
