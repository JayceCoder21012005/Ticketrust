using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using TicketRush.Domain.Events;
using TicketRush.Domain.Gates;
using TicketRush.Domain.Notifications;
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

    DbSet<Notification> Notifications { get; }

    DbSet<EmailDelivery> EmailDeliveries { get; }

    DbSet<EventFollow> EventFollows { get; }

    /// <summary>Sold-out waitlist (spec 13).</summary>
    DbSet<TicketRush.Domain.Waitlist.WaitlistEntry> WaitlistEntries { get; }

    DbSet<ReminderDispatch> ReminderDispatches { get; }

    /// <summary>Resale and transfer (spec 14).</summary>
    DbSet<TicketRush.Domain.Resale.ResaleListing> ResaleListings { get; }

    DbSet<TicketRush.Domain.Resale.TicketTransfer> TicketTransfers { get; }

    DbSet<TicketRush.Domain.Resale.SellerPayout> SellerPayouts { get; }

    /// <summary>Sales per minute per tier (spec 15).</summary>
    DbSet<TicketRush.Domain.Dashboard.SalesMinute> SalesPerMinute { get; }

    /// <summary>The append-only audit log (spec 16): read it, never change it.</summary>
    DbSet<TicketRush.Domain.Audit.AuditEntry> AuditEntries { get; }

    DbSet<TicketRush.Domain.Users.AccountFlag> AccountFlags { get; }

    DbSet<TicketRush.Domain.Users.OrganizerApplication> OrganizerApplications { get; }

    DbSet<GateAssignment> GateAssignments { get; }

    /// <summary>The log of every gate scan (spec 11).</summary>
    DbSet<CheckIn> CheckIns { get; }

    /// <summary>Change tracking access, e.g. to set the original concurrency token a client sent back.</summary>
    EntityEntry<TEntity> Entry<TEntity>(TEntity entity)
        where TEntity : class;

    /// <summary>Lets a use case drop a failed unit of work (e.g. after a concurrency conflict) and reload.</summary>
    ChangeTracker ChangeTracker { get; }

    Task<int> SaveChangesAsync(CancellationToken ct = default);

    /// <summary>
    /// Runs <paramref name="work"/> in one database transaction and commits when it returns; if it throws, everything it
    /// wrote (raw SQL included) is rolled back and the exception surfaces. For use cases that need several statements
    /// (and <see cref="SaveChangesAsync"/> calls) to stand or fall together, such as fulfilling a paid order.
    /// Goes through EF's execution strategy, so it stays correct if connection retries are ever enabled; then
    /// <paramref name="work"/> may run more than once and must start from a clean change tracker.
    /// </summary>
    /// <exception cref="DatabaseContentionException">The transaction lost a deadlock or serialization race (it was rolled back; running it again may succeed).</exception>
    Task<T> InTransactionAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct);

    /// <summary>
    /// Same as <see cref="InTransactionAsync{T}(Func{CancellationToken, Task{T}}, CancellationToken)"/>, with
    /// <c>SET LOCAL lock_timeout</c>: any lock wait longer than <paramref name="lockTimeout"/> fails the transaction.
    /// </summary>
    /// <exception cref="DatabaseLockTimeoutException">A lock wait went over <paramref name="lockTimeout"/> (rolled back).</exception>
    Task<T> InTransactionAsync<T>(Func<CancellationToken, Task<T>> work, TimeSpan lockTimeout, CancellationToken ct);
}

/// <summary>
/// A lock wait of a transaction run with a lock timeout went over it (Postgres <c>55P03</c>); the transaction was rolled back
/// and nothing it wrote is kept. Running it again later may succeed.
/// </summary>
public sealed class DatabaseLockTimeoutException : Exception
{
    public DatabaseLockTimeoutException()
    {
    }

    public DatabaseLockTimeoutException(string message)
        : base(message)
    {
    }

    public DatabaseLockTimeoutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A transaction was chosen as a deadlock victim or failed serialization; it was rolled back and may be retried.</summary>
public sealed class DatabaseContentionException : Exception
{
    public DatabaseContentionException()
    {
    }

    public DatabaseContentionException(string message)
        : base(message)
    {
    }

    public DatabaseContentionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
