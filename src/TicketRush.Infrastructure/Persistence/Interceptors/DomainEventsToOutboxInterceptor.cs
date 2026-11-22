using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TicketRush.Domain.Primitives;
using TicketRush.Infrastructure.Outbox;

namespace TicketRush.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Converts the domain events of tracked aggregates into outbox rows that are inserted by the same
/// <c>SaveChanges</c>, i.e. in the same transaction as the state change (C8: no dual writes).
/// </summary>
public sealed class DomainEventsToOutboxInterceptor(TimeProvider timeProvider) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        WriteOutboxMessages(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        WriteOutboxMessages(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void WriteOutboxMessages(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var aggregates = context.ChangeTracker.Entries<IHasDomainEvents>()
            .Select(entry => entry.Entity)
            .Where(aggregate => aggregate.DomainEvents.Count > 0)
            .ToList();

        var now = timeProvider.GetUtcNow();
        var sequence = 0;

        foreach (var aggregate in aggregates)
        {
            foreach (var domainEvent in aggregate.DequeueDomainEvents())
            {
                // Postgres keeps microseconds: stepping 1 µs per event preserves raise order
                // when several events of one transaction share the same clock reading.
                var occurredAt = now.AddTicks(sequence++ * TimeSpan.TicksPerMicrosecond);
                context.Add(new OutboxMessage(
                    Guid.CreateVersion7(),
                    DomainEventTypeRegistry.NameOf(domainEvent.GetType()),
                    DomainEventTypeRegistry.Serialize(domainEvent),
                    aggregate.Id,
                    occurredAt,
                    Observability.TraceContext.CurrentParent()));
            }
        }
    }
}
