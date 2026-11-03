using Microsoft.EntityFrameworkCore;
using TicketRush.Domain.Events;
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

    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
