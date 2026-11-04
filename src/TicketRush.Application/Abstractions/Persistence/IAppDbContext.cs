using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using TicketRush.Domain.Events;
using TicketRush.Domain.Orders;
using TicketRush.Domain.Tickets;
using TicketRush.Domain.Users;
using TicketRush.Domain.Venues;

namespace TicketRush.Application.Abstractions.Persistence;

/// <summary>
/// Unit of Work. A use case loads aggregates, mutates them and calls <see cref="SaveChangesAsync"/>
/// exactly once; domain events are written to the outbox inside that same transaction.
/// </summary>
public interface IAppDbContext
{
    DbSet<Event> Events { get; }

    DbSet<Venue> Venues { get; }

    DbSet<EventSeat> EventSeats { get; }

    DbSet<RefreshToken> RefreshTokens { get; }

    DbSet<Order> Orders { get; }

    DbSet<PaymentAttempt> PaymentAttempts { get; }

    DbSet<Ticket> Tickets { get; }

    /// <summary>Webhook receipts; inserting the same provider event id twice is a unique violation (spec 08, C4).</summary>
    DbSet<ProcessedWebhook> ProcessedWebhooks { get; }

    /// <summary>Change tracking access, e.g. to set the original concurrency token a client sent back.</summary>
    EntityEntry<TEntity> Entry<TEntity>(TEntity entity)
        where TEntity : class;

    /// <summary>Lets a use case drop a failed unit of work (e.g. after a concurrency conflict) and reload.</summary>
    ChangeTracker ChangeTracker { get; }

    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
