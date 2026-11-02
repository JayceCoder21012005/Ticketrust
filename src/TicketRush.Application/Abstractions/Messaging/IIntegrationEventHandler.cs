using TicketRush.Domain.Primitives;

namespace TicketRush.Application.Abstractions.Messaging;

/// <summary>
/// Consumes a domain event delivered by the outbox dispatcher.
/// Delivery is at-least-once, so every implementation <b>must be idempotent</b>. Database effects made
/// through the scoped <c>IAppDbContext</c> commit atomically with the consumer receipt, which makes them
/// exactly-once; external side effects (email, HTTP) need their own idempotency key — use
/// <see cref="IntegrationEventContext.MessageId"/>.
/// </summary>
public interface IIntegrationEventHandler<in TEvent>
    where TEvent : IDomainEvent
{
    Task HandleAsync(TEvent message, IntegrationEventContext context, CancellationToken ct);
}

/// <param name="MessageId">Outbox message id; stable across retries.</param>
/// <param name="OccurredAt">When the domain event was recorded.</param>
/// <param name="Attempt">1 for the first delivery, incremented on every retry.</param>
public sealed record IntegrationEventContext(Guid MessageId, DateTimeOffset OccurredAt, int Attempt);

/// <summary>Links an event type to a handler type; the dispatcher resolves handlers from these.</summary>
/// <param name="Invoke">Resolves the handler from a scope and calls it with a strongly typed event.</param>
public sealed record IntegrationEventHandlerRegistration(
    Type EventType,
    Type HandlerType,
    Func<IServiceProvider, object, IntegrationEventContext, CancellationToken, Task> Invoke)
{
    /// <summary>Stable name stored in <c>consumer_receipts.handler</c>.</summary>
    public string HandlerName => HandlerType.FullName ?? HandlerType.Name;
}
