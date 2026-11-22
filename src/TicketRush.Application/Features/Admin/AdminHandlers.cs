using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TicketRush.Application.Abstractions.Admin;
using TicketRush.Application.Abstractions.Audit;
using TicketRush.Application.Features.Catalog;
using TicketRush.Application.Abstractions.Inventory;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Application.Options;
using TicketRush.Domain.Audit;
using TicketRush.Domain.Events;
using TicketRush.Domain.Orders;
using TicketRush.Domain.Primitives;
using TicketRush.Domain.Resale;
using TicketRush.Domain.Tickets;
using TicketRush.Domain.Users;

namespace TicketRush.Application.Features.Admin;

// ── Users ────────────────────────────────────────────────────────────────────────────────────────────────────────────────

public sealed record AdminOrderRow(Guid Id, Guid EventId, string EventTitle, OrderStatus Status, OrderKind Kind, long Total, int Tickets, DateTimeOffset CreatedAt, DateTimeOffset? PaidAt);

public sealed record AdminUserDetail(AdminUser User, IReadOnlyList<AdminOrderRow> RecentOrders, int OpenFlags);

public sealed class SearchUsersHandler(IAdminUsers users)
{
    public async Task<Result<AdminPage<AdminUser>>> HandleAsync(string? query, string? cursor, int? limit, CancellationToken ct) =>
        AdminCursor.TryDecode(cursor, out _) ? await users.SearchAsync(query, cursor, AdminCursor.Limit(limit), ct) : AdminErrors.InvalidCursor;
}

/// <summary>A user's profile, roles, lockout, last 20 orders and open risk flags.</summary>
public sealed class GetAdminUserHandler(IAdminUsers users, IAppDbContext db)
{
    public async Task<Result<AdminUserDetail>> HandleAsync(Guid userId, CancellationToken ct)
    {
        if (await users.GetAsync(userId, ct) is not { } user)
        {
            return AdminErrors.UserNotFound;
        }

        var orders = await AdminOrders.Rows(db, db.Orders.Where(o => o.UserId == userId)).Take(20).ToListAsync(ct);
        var flags = await db.AccountFlags.CountAsync(f => f.UserId == userId && f.ResolvedAt == null, ct);
        return new AdminUserDetail(user, orders, flags);
    }
}

public enum UserChange
{
    Lock,
    Unlock,
    Grant,
    Revoke,
}

public sealed record ChangeUserCommand(Guid AdminId, Guid UserId, UserChange Change, string? Role, string? Reason);

/// <summary>
/// Lock / unlock / grant / revoke (spec 16). The audit entry (<c>user.locked</c>, <c>role.granted</c>, …, with the reason) is recorded
/// first and saved by the Identity change itself, so the two commit together. An admin cannot lock themselves or drop their own Admin role.
/// </summary>
public sealed class ChangeUserHandler(IAdminUsers users, IAuditLogger audit)
{
    private static readonly string[] KnownRoles = [Roles.Customer, Roles.Organizer, Roles.Admin, Roles.GateStaff];

    public async Task<Result<AdminUser>> HandleAsync(ChangeUserCommand command, CancellationToken ct)
    {
        if (AdminErrors.CleanReason(command.Reason) is not { } reason)
        {
            return AdminErrors.ReasonRequired;
        }

        var role = command.Change is UserChange.Grant or UserChange.Revoke ? KnownRoles.FirstOrDefault(r => string.Equals(r, command.Role, StringComparison.OrdinalIgnoreCase)) : null;
        if (command.Change is UserChange.Grant or UserChange.Revoke && role is null)
        {
            return AdminErrors.InvalidRole;
        }

        if (command.AdminId == command.UserId && (command.Change == UserChange.Lock || (command.Change == UserChange.Revoke && role == Roles.Admin)))
        {
            return AdminErrors.OwnAdminRole;
        }

        if (await users.GetAsync(command.UserId, ct) is null)
        {
            return AdminErrors.UserNotFound;
        }

        var (action, details) = command.Change switch
        {
            UserChange.Lock => ("user.locked", (object)new { reason }),
            UserChange.Unlock => ("user.unlocked", new { reason }),
            UserChange.Grant => ("role.granted", new { role, reason }),
            _ => ("role.revoked", new { role, reason }),
        };
        audit.Record(action, "user", command.UserId.ToString(), details);
        var changed = command.Change switch
        {
            UserChange.Lock => await users.LockAsync(command.UserId, ct),
            UserChange.Unlock => await users.UnlockAsync(command.UserId, ct),
            UserChange.Grant => await users.GrantRoleAsync(command.UserId, role!, ct),
            _ => await users.RevokeRoleAsync(command.UserId, role!, ct),
        };
        return changed.IsFailure ? changed.Error : (await users.GetAsync(command.UserId, ct))!;
    }
}

// ── Organizer applications ────────────────────────────────────────────────────────────────────────────────────────────────

public sealed record OrganizerApplicationView(
    Guid Id, Guid UserId, string CompanyName, string Phone, string Description, OrganizerApplicationStatus Status, string? DecisionReason, DateTimeOffset CreatedAt, DateTimeOffset? ReviewedAt);

internal static class Applications
{
    public static OrganizerApplicationView View(OrganizerApplication a) =>
        new(a.Id, a.UserId, a.CompanyName, a.Phone, a.Description, a.Status, a.DecisionReason, a.CreatedAt, a.ReviewedAt);
}

public sealed record SubmitApplicationCommand(Guid UserId, bool IsOrganizer, string? CompanyName, string? Phone, string? Description);

/// <summary><c>POST /organizer-applications</c>: one pending application per user (a unique partial index settles a double submit).</summary>
public sealed class SubmitOrganizerApplicationHandler(IAppDbContext db)
{
    public async Task<Result<OrganizerApplicationView>> HandleAsync(SubmitApplicationCommand command, CancellationToken ct)
    {
        if (command.IsOrganizer)
        {
            return AdminErrors.AlreadyOrganizer;
        }

        if (string.IsNullOrWhiteSpace(command.CompanyName) || string.IsNullOrWhiteSpace(command.Phone)
            || command.CompanyName.Length > OrganizerApplication.CompanyNameMaxLength || command.Phone.Trim().Length > 32
            || (command.Description?.Length ?? 0) > OrganizerApplication.DescriptionMaxLength)
        {
            return AdminErrors.InvalidApplication;
        }

        if (await db.OrganizerApplications.AnyAsync(a => a.UserId == command.UserId && a.Status == OrganizerApplicationStatus.Pending, ct))
        {
            return AdminErrors.AlreadyApplied;
        }

        var application = OrganizerApplication.Submit(command.UserId, command.CompanyName, command.Phone, command.Description ?? string.Empty);
        db.OrganizerApplications.Add(application);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return AdminErrors.AlreadyApplied;
        }

        return Applications.View(application);
    }
}

/// <summary><c>GET /me/organizer-application</c>: the caller's latest application, or 404.</summary>
public sealed class GetMyOrganizerApplicationHandler(IAppDbContext db)
{
    public async Task<Result<OrganizerApplicationView>> HandleAsync(Guid userId, CancellationToken ct) =>
        await db.OrganizerApplications.AsNoTracking().Where(a => a.UserId == userId).OrderByDescending(a => a.CreatedAt).FirstOrDefaultAsync(ct) is { } application
            ? Applications.View(application)
            : AdminErrors.ApplicationNotFound;
}

public sealed class ListOrganizerApplicationsHandler(IAppDbContext db)
{
    public async Task<Result<AdminPage<OrganizerApplicationView>>> HandleAsync(OrganizerApplicationStatus? status, string? cursor, int? limit, CancellationToken ct)
    {
        if (!AdminCursor.TryDecode(cursor, out var after))
        {
            return AdminErrors.InvalidCursor;
        }

        var take = AdminCursor.Limit(limit);
        var query = db.OrganizerApplications.AsNoTracking();
        if (status is { } wanted)
        {
            query = query.Where(a => a.Status == wanted);
        }

        if (after is { } c)
        {
            query = query.Where(a => a.CreatedAt < c.At || (a.CreatedAt == c.At && a.Id.CompareTo(c.Id) < 0));
        }

        var rows = await query.OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id).Take(take + 1).ToListAsync(ct);
        return new AdminPage<OrganizerApplicationView>(
            [.. rows.Take(take).Select(Applications.View)], rows.Count > take ? AdminCursor.Encode(rows[take - 1].CreatedAt, rows[take - 1].Id) : null);
    }
}

public sealed record DecideApplicationCommand(Guid AdminId, Guid ApplicationId, bool Approve, string? Reason);

/// <summary>Approve (grants Organizer) or reject, with a reason; the applicant is emailed through <see cref="OrganizerApplicationDecided"/>.</summary>
public sealed class DecideOrganizerApplicationHandler(IAppDbContext db, IAdminUsers users, IAuditLogger audit, TimeProvider time)
{
    public async Task<Result<OrganizerApplicationView>> HandleAsync(DecideApplicationCommand command, CancellationToken ct)
    {
        if (AdminErrors.CleanReason(command.Reason) is not { } reason)
        {
            return AdminErrors.ReasonRequired;
        }

        var application = await db.OrganizerApplications.FirstOrDefaultAsync(a => a.Id == command.ApplicationId, ct);
        if (application is null)
        {
            return AdminErrors.ApplicationNotFound;
        }

        if (application.Status != OrganizerApplicationStatus.Pending)
        {
            return AdminErrors.WrongState;
        }

        var now = time.GetUtcNow();
        if (command.Approve)
        {
            application.Approve(command.AdminId, reason, now);
            audit.Record("organizer_application.approved", "organizer_application", application.Id.ToString(), new { userId = application.UserId, reason });
            // The role grant saves the application, its domain event and the audit entry with it.
            var granted = await users.GrantRoleAsync(application.UserId, Roles.Organizer, ct);
            if (granted.IsFailure)
            {
                return granted.Error;
            }
        }
        else
        {
            application.Reject(command.AdminId, reason, now);
            audit.Record("organizer_application.rejected", "organizer_application", application.Id.ToString(), new { userId = application.UserId, reason });
            await db.SaveChangesAsync(ct);
        }

        return Applications.View(application);
    }
}

// ── Events ───────────────────────────────────────────────────────────────────────────────────────────────────────────────

public sealed record AdminEventRow(Guid Id, string Slug, string Title, Guid OrganizerId, string City, DateTimeOffset StartsAt, EventStatus Status, DateTimeOffset CreatedAt);

public sealed class ListAdminEventsHandler(IAppDbContext db)
{
    public async Task<Result<AdminPage<AdminEventRow>>> HandleAsync(string? query, EventStatus? status, string? cursor, int? limit, CancellationToken ct)
    {
        if (!AdminCursor.TryDecode(cursor, out var after))
        {
            return AdminErrors.InvalidCursor;
        }

        var take = AdminCursor.Limit(limit);
        var events = db.Events.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query))
        {
            var q = query.Trim();
            var lower = q.ToLowerInvariant();
#pragma warning disable CA1304, CA1311, CA1862 // Translated to SQL lower(); the culture-aware overloads are not.
            events = Guid.TryParse(q, out var id) ? events.Where(e => e.Id == id) : events.Where(e => e.Title.ToLower().Contains(lower) || e.Slug.Contains(lower));
#pragma warning restore CA1304, CA1311, CA1862
        }

        if (status is { } wanted)
        {
            events = events.Where(e => e.Status == wanted);
        }

        if (after is { } c)
        {
            events = events.Where(e => e.CreatedAt < c.At || (e.CreatedAt == c.At && e.Id.CompareTo(c.Id) < 0));
        }

        var rows = await events.OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id).Take(take + 1)
            .Select(e => new AdminEventRow(e.Id, e.Slug, e.Title, e.OrganizerId, e.City, e.StartsAt, e.Status, e.CreatedAt))
            .ToListAsync(ct);
        return new AdminPage<AdminEventRow>([.. rows.Take(take)], rows.Count > take ? AdminCursor.Encode(rows[take - 1].CreatedAt, rows[take - 1].Id) : null);
    }
}

public sealed record ModerateEventCommand(Guid EventId, bool Suspend, string? Reason);

/// <summary>
/// Suspend / restore (spec 16 moderation). Like a cancellation, the status change takes the event's sale lock exclusively so a
/// payment being confirmed commits first; the catalog caches are dropped and the organizer is told (<see cref="EventSuspended"/>).
/// </summary>
public sealed class ModerateEventHandler(IAppDbContext db, IEventSaleLock saleLock, ICatalogCacheInvalidator cache, IAuditLogger audit, IOptions<FulfilmentOptions> options)
{
    public async Task<Result<AdminEventRow>> HandleAsync(ModerateEventCommand command, CancellationToken ct)
    {
        if (AdminErrors.CleanReason(command.Reason) is not { } reason)
        {
            return AdminErrors.ReasonRequired;
        }

        try
        {
            var (evt, changed) = await db.InTransactionAsync<(Event?, bool)>(async token =>
            {
                var found = await db.Events.FirstOrDefaultAsync(e => e.Id == command.EventId, token);
                if (found is null || found.Status != (command.Suspend ? EventStatus.Published : EventStatus.Suspended))
                {
                    return (found, false);
                }

                await saleLock.AcquireExclusiveAsync(found.Id, token);
                if (command.Suspend)
                {
                    found.Suspend(reason);
                }
                else
                {
                    found.Restore(reason);
                }

                audit.Record(command.Suspend ? "event.suspended" : "event.restored", "event", found.Id.ToString(), new { reason });
                await db.SaveChangesAsync(token);
                return (found, true);
            }, options.Value.LockTimeout, ct);

            if (evt is null)
            {
                return AdminErrors.EventNotFound;
            }

            if (!changed)
            {
                // Already suspended (or not suspended): nothing to do, and saying so beats a silent success.
                return AdminErrors.WrongState;
            }

            await cache.InvalidateEventAsync(evt.Id, evt.Slug, ct);
            return new AdminEventRow(evt.Id, evt.Slug, evt.Title, evt.OrganizerId, evt.City, evt.StartsAt, evt.Status, evt.CreatedAt);
        }
        catch (DatabaseLockTimeoutException)
        {
            return EventErrors.CancelBusy;
        }
    }
}

// ── Orders ───────────────────────────────────────────────────────────────────────────────────────────────────────────────

public sealed record AdminTicketRow(Guid Id, Guid TierId, int? SeatIndex, Guid OwnerUserId, TicketStatus Status);

public sealed record AdminOrderDetail(
    Guid Id, Guid UserId, Guid EventId, string EventTitle, OrderStatus Status, OrderKind Kind, long Total, string Currency, string BuyerName, string BuyerEmail,
    string? BuyerPhone, string? RefundReason, DateTimeOffset CreatedAt, DateTimeOffset? PaidAt, IReadOnlyList<AdminTicketRow> Tickets);

internal static class AdminOrders
{
    public static IQueryable<AdminOrderRow> Rows(IAppDbContext db, IQueryable<Order> orders) =>
        from o in orders.AsNoTracking()
        join e in db.Events.AsNoTracking() on o.EventId equals e.Id
        orderby o.CreatedAt descending, o.Id descending
        select new AdminOrderRow(o.Id, o.EventId, e.Title, o.Status, o.Kind, o.Total.Amount, o.Items.Sum(i => i.Quantity), o.CreatedAt, o.PaidAt);
}

/// <summary>Looks orders up by id, buyer email or phone (exact), newest first.</summary>
public sealed class SearchAdminOrdersHandler(IAppDbContext db)
{
    public async Task<Result<AdminPage<AdminOrderRow>>> HandleAsync(string? query, string? cursor, int? limit, CancellationToken ct)
    {
        if (!AdminCursor.TryDecode(cursor, out var after))
        {
            return AdminErrors.InvalidCursor;
        }

        var take = AdminCursor.Limit(limit);
        var orders = db.Orders.AsQueryable();
        if (!string.IsNullOrWhiteSpace(query))
        {
            var q = query.Trim();
            var email = q.ToLowerInvariant();
            orders = Guid.TryParse(q, out var id)
                ? orders.Where(o => o.Id == id)
#pragma warning disable CA1304, CA1311, CA1862 // Translated to SQL lower(); the culture-aware overloads are not.
                : orders.Where(o => o.Buyer.Email.ToLower() == email || o.Buyer.Phone == q);
#pragma warning restore CA1304, CA1311, CA1862
        }

        if (after is { } c)
        {
            orders = orders.Where(o => o.CreatedAt < c.At || (o.CreatedAt == c.At && o.Id.CompareTo(c.Id) < 0));
        }

        var rows = await AdminOrders.Rows(db, orders).Take(take + 1).ToListAsync(ct);
        return new AdminPage<AdminOrderRow>([.. rows.Take(take)], rows.Count > take ? AdminCursor.Encode(rows[take - 1].CreatedAt, rows[take - 1].Id) : null);
    }
}

public sealed class GetAdminOrderHandler(IAppDbContext db)
{
    public async Task<Result<AdminOrderDetail>> HandleAsync(Guid orderId, CancellationToken ct)
    {
        var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null)
        {
            return AdminErrors.OrderNotFound;
        }

        var title = await db.Events.AsNoTracking().Where(e => e.Id == order.EventId).Select(e => e.Title).FirstOrDefaultAsync(ct) ?? string.Empty;
        var tickets = await db.Tickets.AsNoTracking().Where(t => t.OrderId == orderId).OrderBy(t => t.Id)
            .Select(t => new AdminTicketRow(t.Id, t.TierId, t.SeatIndex, t.OwnerUserId, t.Status)).ToListAsync(ct);
        return new AdminOrderDetail(order.Id, order.UserId, order.EventId, title, order.Status, order.Kind, order.Total.Amount, order.Total.Currency,
            order.Buyer.Name, order.Buyer.Email, order.Buyer.Phone, order.RefundReason, order.CreatedAt, order.PaidAt, tickets);
    }
}

public sealed record AdminRefundCommand(Guid OrderId, string? Reason, string? Confirm);

/// <summary>
/// Manual refund (spec 16): a paid primary order goes to RefundPending (08's refund worker pays it back, the buyer is notified by
/// its <c>RefundRequested</c> notice) and each of its live tickets is voided, which hands the seat to the waitlist (13's
/// <see cref="TicketReturned"/>). A listing or transfer of those tickets ends first. Refused when a ticket was already used at the
/// gate, and for resale orders. Double confirmation: <c>confirm</c> must repeat the order id.
/// </summary>
public sealed class AdminRefundHandler(IAppDbContext db, IAuditLogger audit, GetAdminOrderHandler details)
{
    public async Task<Result<AdminOrderDetail>> HandleAsync(AdminRefundCommand command, CancellationToken ct)
    {
        if (AdminErrors.CleanReason(command.Reason) is not { } reason)
        {
            return AdminErrors.ReasonRequired;
        }

        if (!string.Equals(command.Confirm?.Trim(), command.OrderId.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return AdminErrors.ConfirmMismatch;
        }

        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == command.OrderId, ct);
        if (order is null)
        {
            return AdminErrors.OrderNotFound;
        }

        var tickets = await db.Tickets.Where(t => t.OrderId == order.Id && t.Status != TicketStatus.Voided).ToListAsync(ct);
        if (order.Status != OrderStatus.Paid || order.Kind != OrderKind.Primary || tickets.Any(t => t.Status == TicketStatus.CheckedIn))
        {
            return AdminErrors.NotRefundable;
        }

        var listed = tickets.Where(t => t.Status == TicketStatus.Listed).Select(t => t.Id).ToList();
        if (listed.Count > 0)
        {
            var listings = await db.ResaleListings.Where(l => listed.Contains(l.TicketId) && (l.Status == ResaleListingStatus.Active || l.Status == ResaleListingStatus.Reserved)).ToListAsync(ct);
            if (listings.Any(l => l.Status == ResaleListingStatus.Reserved))
            {
                // Someone is paying for one of these tickets right now.
                return AdminErrors.NotRefundable;
            }

            listings.ForEach(l => l.CloseForCancelledEvent());
            (await db.TicketTransfers.Where(t => listed.Contains(t.TicketId) && t.Status == TicketTransferStatus.Pending).ToListAsync(ct))
                .ForEach(t => t.CloseForCancelledEvent());
        }

        order.RequestAdminRefund();
        tickets.ForEach(t => t.Void());
        audit.Record("order.refund_manual", "order", order.Id.ToString(), new { reason, tickets = tickets.Count, amount = order.Total.Amount });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return AdminErrors.WrongState;
        }

        return await details.HandleAsync(order.Id, ct);
    }
}

// ── Risk flags ───────────────────────────────────────────────────────────────────────────────────────────────────────────

public sealed record AdminFlagRow(Guid Id, Guid UserId, string Reason, string Detail, DateTimeOffset CreatedAt, DateTimeOffset? ResolvedAt, string? Resolution);

public sealed class ListFlagsHandler(IAppDbContext db)
{
    public async Task<Result<AdminPage<AdminFlagRow>>> HandleAsync(bool open, string? cursor, int? limit, CancellationToken ct)
    {
        if (!AdminCursor.TryDecode(cursor, out var after))
        {
            return AdminErrors.InvalidCursor;
        }

        var take = AdminCursor.Limit(limit);
        var flags = db.AccountFlags.AsNoTracking().Where(f => open ? f.ResolvedAt == null : f.ResolvedAt != null);
        if (after is { } c)
        {
            flags = flags.Where(f => f.CreatedAt < c.At || (f.CreatedAt == c.At && f.Id.CompareTo(c.Id) < 0));
        }

        var rows = await flags.OrderByDescending(f => f.CreatedAt).ThenByDescending(f => f.Id).Take(take + 1)
            .Select(f => new AdminFlagRow(f.Id, f.UserId, f.Reason, f.Detail, f.CreatedAt, f.ResolvedAt, f.Resolution)).ToListAsync(ct);
        return new AdminPage<AdminFlagRow>([.. rows.Take(take)], rows.Count > take ? AdminCursor.Encode(rows[take - 1].CreatedAt, rows[take - 1].Id) : null);
    }
}

public sealed record ResolveFlagCommand(Guid AdminId, Guid FlagId, bool Lock, string? Reason);

/// <summary>Dismiss a flag, or lock its account (and resolve the flag), with a reason.</summary>
public sealed class ResolveFlagHandler(IAppDbContext db, IAdminUsers users, IAuditLogger audit, TimeProvider time)
{
    public async Task<Result<AdminFlagRow>> HandleAsync(ResolveFlagCommand command, CancellationToken ct)
    {
        if (AdminErrors.CleanReason(command.Reason) is not { } reason)
        {
            return AdminErrors.ReasonRequired;
        }

        var flag = await db.AccountFlags.FirstOrDefaultAsync(f => f.Id == command.FlagId, ct);
        if (flag is null)
        {
            return AdminErrors.FlagNotFound;
        }

        if (flag.ResolvedAt is not null)
        {
            return AdminErrors.WrongState;
        }

        if (command.Lock && flag.UserId == command.AdminId)
        {
            return AdminErrors.OwnAdminRole;
        }

        flag.Resolve(command.Lock ? $"locked: {reason}" : $"dismissed: {reason}", time.GetUtcNow());
        audit.Record(command.Lock ? "flag.locked" : "flag.dismissed", "account_flag", flag.Id.ToString(), new { userId = flag.UserId, reason });
        if (command.Lock)
        {
            // The lock saves the flag and the audit entry with it.
            var locked = await users.LockAsync(flag.UserId, ct);
            if (locked.IsFailure)
            {
                return locked.Error;
            }
        }
        else
        {
            await db.SaveChangesAsync(ct);
        }

        return new AdminFlagRow(flag.Id, flag.UserId, flag.Reason, flag.Detail, flag.CreatedAt, flag.ResolvedAt, flag.Resolution);
    }
}

// ── Outbox ───────────────────────────────────────────────────────────────────────────────────────────────────────────────

public sealed class ListDeadLettersHandler(IAdminOutbox outbox)
{
    public async Task<Result<AdminPage<DeadLetter>>> HandleAsync(string? cursor, int? limit, CancellationToken ct) =>
        AdminCursor.TryDecode(cursor, out _) ? await outbox.ListAsync(cursor, AdminCursor.Limit(limit), ct) : AdminErrors.InvalidCursor;
}

public sealed record DeadLetterCommand(Guid MessageId, bool Retry, string? Reason);

public sealed class ResolveDeadLetterHandler(IAdminOutbox outbox, IAuditLogger audit)
{
    public async Task<Result> HandleAsync(DeadLetterCommand command, CancellationToken ct)
    {
        if (AdminErrors.CleanReason(command.Reason) is not { } reason)
        {
            return AdminErrors.ReasonRequired;
        }

        void Record() => audit.Record(command.Retry ? "outbox.retried" : "outbox.discarded", "outbox_message", command.MessageId.ToString(), new { reason });
        var done = command.Retry ? await outbox.RetryAsync(command.MessageId, Record, ct) : await outbox.DiscardAsync(command.MessageId, reason, Record, ct);
        return done ? Result.Success() : AdminErrors.DeadLetterNotFound;
    }
}

// ── Audit ────────────────────────────────────────────────────────────────────────────────────────────────────────────────

public sealed record AuditQuery(Guid? Actor, string? EntityType, string? EntityId, string? Action, DateTimeOffset? From, DateTimeOffset? To, string? Cursor, int? Limit);

public sealed record AuditRow(Guid Id, DateTimeOffset At, Guid? ActorUserId, string? ActorIp, string Action, string EntityType, string? EntityId, string? Changes, string? CorrelationId);

public sealed class ListAuditHandler(IAppDbContext db)
{
    public async Task<Result<AdminPage<AuditRow>>> HandleAsync(AuditQuery query, CancellationToken ct)
    {
        if (!AdminCursor.TryDecode(query.Cursor, out var after))
        {
            return AdminErrors.InvalidCursor;
        }

        var take = AdminCursor.Limit(query.Limit);
        var entries = db.AuditEntries.AsNoTracking();
        if (query.Actor is { } actor)
        {
            entries = entries.Where(a => a.ActorUserId == actor);
        }

        if (!string.IsNullOrWhiteSpace(query.EntityType))
        {
            entries = entries.Where(a => a.EntityType == query.EntityType.Trim());
        }

        if (!string.IsNullOrWhiteSpace(query.EntityId))
        {
            entries = entries.Where(a => a.EntityId == query.EntityId.Trim());
        }

        if (!string.IsNullOrWhiteSpace(query.Action))
        {
            entries = entries.Where(a => a.Action.StartsWith(query.Action.Trim()));
        }

        if (query.From is { } from)
        {
            entries = entries.Where(a => a.At >= from);
        }

        if (query.To is { } to)
        {
            entries = entries.Where(a => a.At < to);
        }

        if (after is { } c)
        {
            entries = entries.Where(a => a.At < c.At || (a.At == c.At && a.Id.CompareTo(c.Id) < 0));
        }

        var rows = await entries.OrderByDescending(a => a.At).ThenByDescending(a => a.Id).Take(take + 1).ToListAsync(ct);
        return new AdminPage<AuditRow>([.. rows.Take(take).Select(Row)], rows.Count > take ? AdminCursor.Encode(rows[take - 1].At, rows[take - 1].Id) : null);
    }

    internal static AuditRow Row(AuditEntry a) => new(a.Id, a.At, a.ActorUserId, a.ActorIp, a.Action, a.EntityType, a.EntityId, a.Changes, a.CorrelationId);
}

public sealed class GetAuditEntryHandler(IAppDbContext db)
{
    public async Task<Result<AuditRow>> HandleAsync(Guid id, CancellationToken ct) =>
        await db.AuditEntries.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct) is { } entry ? ListAuditHandler.Row(entry) : AdminErrors.AuditNotFound;
}
