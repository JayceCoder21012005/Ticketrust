using Microsoft.EntityFrameworkCore;
using TicketRush.Application.Abstractions.Persistence;

namespace TicketRush.Application.Features.Gate;

internal static class GateAssignments
{
    /// <returns>The gate name of the staff member's assignment to the event, or null when not assigned.</returns>
    public static Task<string?> FindGateNameAsync(IAppDbContext db, Guid eventId, Guid staffUserId, CancellationToken ct) =>
        db.GateAssignments.AsNoTracking()
            .Where(a => a.EventId == eventId && a.UserId == staffUserId)
            .Select(a => a.GateName)
            .FirstOrDefaultAsync(ct);
}
