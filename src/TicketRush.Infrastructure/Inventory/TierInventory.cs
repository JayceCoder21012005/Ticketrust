using Microsoft.EntityFrameworkCore;
using TicketRush.Application.Abstractions.Inventory;
using TicketRush.Infrastructure.Persistence;

namespace TicketRush.Infrastructure.Inventory;

/// <summary>
/// One conditional UPDATE decides, atomically in Postgres, whether a general-admission claim still fits the tier's
/// capacity (spec 08, C1). It runs on the context's current transaction, so the caller's rollback undoes the claim.
/// </summary>
public sealed class TierInventory(AppDbContext db) : ITierInventory
{
    public async Task<bool> TryClaimAsync(Guid tierId, int quantity, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);

        // A seated tier has a null capacity, so the comparison is never true and nothing is claimed.
        var updated = await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE ticket_tiers SET sold_count = sold_count + {quantity} WHERE id = {tierId} AND sold_count + {quantity} <= capacity",
            ct);
        return updated == 1;
    }
}
