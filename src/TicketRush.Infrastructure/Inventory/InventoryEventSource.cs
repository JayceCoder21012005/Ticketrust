using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TicketRush.Application.Abstractions.Inventory;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Domain.Events;

namespace TicketRush.Infrastructure.Inventory;

/// <summary>Published events with a phase open now or opening within the lookahead. Singleton-safe (own scope per call).</summary>
public sealed class InventoryEventSource(IServiceScopeFactory scopes) : IInventoryEventSource
{
    private static readonly TimeSpan ClosedGrace = TimeSpan.FromMinutes(15);

    public async Task<IReadOnlyList<LiveInventoryEvent>> ListLiveAsync(DateTimeOffset now, TimeSpan lookahead, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var opensBefore = now + lookahead;
        // Holds outlive the phase by up to their TTL, so swept events stay live a little after closing.
        var closedAfter = now - ClosedGrace;

        var events = await db.Events
            .AsNoTracking()
            .Where(e => e.Status == EventStatus.Published && e.Phases.Any(p => p.OpensAt <= opensBefore && p.ClosesAt > closedAfter))
            .Select(e => new
            {
                e.Id,
                Ga = e.Tiers.Where(t => t.Kind == TicketKind.GeneralAdmission).Select(t => t.Id).ToList(),
            })
            .ToListAsync(ct);

        return [.. events.Select(e => new LiveInventoryEvent(e.Id, e.Ga))];
    }
}
