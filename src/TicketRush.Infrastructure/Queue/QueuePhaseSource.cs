using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Application.Abstractions.Queue;
using TicketRush.Domain.Events;

namespace TicketRush.Infrastructure.Queue;

/// <summary>Singleton-safe: opens its own scope per call because the DbContext is scoped.</summary>
public sealed class QueuePhaseSource(IServiceScopeFactory scopes) : IQueuePhaseSource
{
    private static readonly TimeSpan ClosedGrace = TimeSpan.FromMinutes(1);

    public async Task<IReadOnlyList<SaleWindow>> ListLiveAsync(DateTimeOffset now, TimeSpan lookahead, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var opensBefore = now + lookahead;
        var closedAfter = now - ClosedGrace;

        return await db.Events
            .Where(e => e.Status == EventStatus.Published)
            .SelectMany(e => e.Phases)
            .Where(p => p.QueueEnabled && p.OpensAt <= opensBefore && p.ClosesAt > closedAfter)
            .Select(p => new SaleWindow(p.EventId, p.Id, p.OpensAt, p.ClosesAt, p.QueueEnabled))
            .ToListAsync(ct);
    }
}
