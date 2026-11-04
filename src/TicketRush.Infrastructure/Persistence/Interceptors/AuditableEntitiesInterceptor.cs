using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TicketRush.Domain.Events;
using TicketRush.Domain.Primitives;

namespace TicketRush.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Stamps <see cref="IAuditable"/> timestamps so domain code never touches the clock for auditing. A change to an
/// event's tiers or phases also counts as a change to the event: its row is written, so the xmin version is checked
/// and bumped even when only child rows changed (spec 05: stale tabs, and edits racing a publish, must fail).
/// </summary>
public sealed class AuditableEntitiesInterceptor(TimeProvider timeProvider) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Stamp(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Stamp(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Stamp(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        context.ChangeTracker.DetectChanges();
        var now = timeProvider.GetUtcNow();
        TouchEventsWithChangedParts(context);

        foreach (var entry in context.ChangeTracker.Entries<IAuditable>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Property(nameof(IAuditable.CreatedAt)).CurrentValue = now;
                entry.Property(nameof(IAuditable.UpdatedAt)).CurrentValue = now;
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Property(nameof(IAuditable.UpdatedAt)).CurrentValue = now;
            }
        }
    }

    // Seats are left out on purpose: later specs change seat rows on the hot path and must not serialize on the event.
    private static void TouchEventsWithChangedParts(DbContext context)
    {
        var changed = context.ChangeTracker.Entries()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Select(entry => entry.Entity switch
            {
                TicketTier tier => tier.EventId,
                SalePhase phase => phase.EventId,
                _ => (Guid?)null,
            })
            .OfType<Guid>()
            .ToHashSet();
        if (changed.Count == 0)
        {
            return;
        }

        foreach (var entry in context.ChangeTracker.Entries<Event>())
        {
            if (entry.State == EntityState.Unchanged && changed.Contains(entry.Entity.Id))
            {
                entry.Property(e => e.UpdatedAt).IsModified = true;
            }
        }
    }
}
