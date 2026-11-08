using Microsoft.EntityFrameworkCore;

namespace TicketRush.Infrastructure.Inventory;

/// <summary>
/// The "is this event still on sale" lock (spec 08, C3): a transaction-scoped Postgres advisory lock per event instead of a
/// row lock on <c>events</c>, so in-flight fulfilments never touch the hot event tuple and organizer edits are not blocked.
/// Fulfilments take it <b>shared</b> (they never wait on each other); whatever ends the sale (cancel, mark ended) takes it
/// <b>exclusive</b> in the same transaction that changes the status, so a fulfilment either commits before the change is
/// made or reads the new status after it committed. B9's EventCancelled handler must call <see cref="AcquireExclusiveAsync"/>.
/// <para>
/// Key derivation: <c>hashtextextended('event-sale:' || &lt;event id as 32 hex digits&gt;, 0)</c> (64-bit). The per-buyer
/// purchase-limit lock uses the namespace <c>ticket-limit:</c>, so the two never share a key by construction other than a
/// 64-bit hash collision. Lock order everywhere: event sale lock → buyer limit lock → GA tier rows → seat index entries.
/// </para>
/// Both calls need an open transaction (the lock is released at commit or rollback).
/// </summary>
public static class EventSaleLocks
{
    public static string KeyOf(Guid eventId) => $"event-sale:{eventId:N}";

    public static Task AcquireSharedAsync(DbContext db, Guid eventId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock_shared(hashtextextended({KeyOf(eventId)}, 0))", ct);
    }

    public static Task AcquireExclusiveAsync(DbContext db, Guid eventId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({KeyOf(eventId)}, 0))", ct);
    }
}
