using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using TicketRush.Application.Abstractions.Notifications;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Application.Features.Tickets;
using TicketRush.Domain.Primitives;
using TicketRush.Domain.Resale;
using TicketRush.Domain.Tickets;

namespace TicketRush.Application.Features.Resale;

public sealed record CreateTransferCommand(Guid UserId, Guid TicketId, string ToEmail);

internal static class TransferReads
{
    public static TransferResponse ToResponse(TicketTransfer t) => new(t.Id, t.TicketId, t.EventId, t.ToEmail, t.Status, t.ExpiresAt, t.CreatedAt);

    /// <summary>The transfer when <paramref name="userId"/> is its recipient: a confirmed account with the email it was sent to.</summary>
    public static async Task<TicketTransfer?> ForRecipientAsync(
        IAppDbContext db, IUserDirectory users, Guid transferId, Guid userId, bool tracked, CancellationToken ct)
    {
        var query = tracked ? db.TicketTransfers : db.TicketTransfers.AsNoTracking();
        var transfer = await query.FirstOrDefaultAsync(t => t.Id == transferId, ct);
        if (transfer is null)
        {
            return null;
        }

        var contact = await users.GetContactAsync(userId, ct);
        return contact is { EmailConfirmed: true } && TicketTransfer.NormalizeEmail(contact.Email) == transfer.ToEmail ? transfer : null;
    }

    public static async Task<TransferInvite?> InviteAsync(IAppDbContext db, IUserDirectory users, TicketTransfer transfer, Guid userId, CancellationToken ct)
    {
        var old = await db.Tickets.AsNoTracking().Where(t => t.Id == transfer.TicketId)
            .Select(t => new { t.OrderId, t.TierId, t.SeatIndex }).FirstOrDefaultAsync(ct);
        var evt = await db.Events.AsNoTracking().Where(e => e.Id == transfer.EventId)
            .Select(e => new { e.Slug, e.Title, e.StartsAt, e.VenueId }).FirstOrDefaultAsync(ct);
        if (old is null || evt is null)
        {
            return null;
        }

        var venue = await db.Venues.AsNoTracking().Where(v => v.Id == evt.VenueId).Select(v => v.Name).FirstOrDefaultAsync(ct) ?? string.Empty;
        var tier = await db.Events.AsNoTracking().Where(e => e.Id == transfer.EventId).SelectMany(e => e.Tiers)
            .Where(t => t.Id == old.TierId).Select(t => t.Name).FirstOrDefaultAsync(ct) ?? string.Empty;
        var from = (await users.GetContactAsync(transfer.FromUserId, ct))?.FullName ?? string.Empty;
        Guid? newTicket = transfer.Status == TicketTransferStatus.Accepted && transfer.ToUserId == userId
            ? await db.Tickets.AsNoTracking()
                .Where(t => t.EventId == transfer.EventId && t.OwnerUserId == userId && t.OrderId == old.OrderId && t.TierId == old.TierId
                    && t.SeatIndex == old.SeatIndex && t.Status != TicketStatus.Voided)
                .Select(t => (Guid?)t.Id).FirstOrDefaultAsync(ct)
            : null;
        return new TransferInvite(
            transfer.Id, transfer.Status, transfer.ExpiresAt, from, transfer.EventId, evt.Slug, evt.Title, evt.StartsAt, venue, tier,
            await ResaleReads.SeatLabelAsync(db, transfer.EventId, old.SeatIndex, ct), newTicket);
    }
}

/// <summary>
/// <c>POST /tickets/{id}/transfers</c> (spec 14): the owner offers a Valid ticket to an email for 48 hours; the recipient is emailed
/// (09, through <see cref="TransferCreated"/>). Not to your own email. The ticket's <c>xmin</c> settles a race with a listing.
/// </summary>
public sealed class CreateTransferHandler(IAppDbContext db, IUserDirectory users, TimeProvider time)
{
    public async Task<Result<TransferResponse>> HandleAsync(CreateTransferCommand command, CancellationToken ct)
    {
        if (!IsEmail(command.ToEmail))
        {
            return TransferErrors.InvalidEmail;
        }

        var ticket = await db.Tickets.FirstOrDefaultAsync(t => t.Id == command.TicketId && t.OwnerUserId == command.UserId, ct);
        if (ticket is null)
        {
            return TicketErrors.NotFound;
        }

        var now = time.GetUtcNow();
        var from = await users.GetContactAsync(command.UserId, ct);
        if (from is null || ticket.Status != TicketStatus.Valid || !await ResaleReads.IsOpenAsync(db, ticket.EventId, now, ct))
        {
            return TransferErrors.NotAllowed;
        }

        TicketTransfer transfer;
        try
        {
            transfer = TicketTransfer.Create(ticket, from.Email, command.ToEmail, now);
        }
        catch (DomainException ex) when (ex.Code == "transfer.self")
        {
            return TransferErrors.Self;
        }

        db.TicketTransfers.Add(transfer);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return TransferErrors.NotAllowed;
        }

        return TransferReads.ToResponse(transfer);
    }

    private static bool IsEmail(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= 320 && MailAddress.TryCreate(value.Trim(), out var parsed) && parsed.Address == value.Trim();
}

/// <summary><c>DELETE /transfers/{id}</c>: the sender cancels a pending transfer; the ticket is Valid again. Idempotent once cancelled.</summary>
public sealed class CancelTransferHandler(IAppDbContext db)
{
    public async Task<Result> HandleAsync(Guid userId, Guid transferId, CancellationToken ct)
    {
        var transfer = await db.TicketTransfers.FirstOrDefaultAsync(t => t.Id == transferId && t.FromUserId == userId, ct);
        if (transfer is null)
        {
            return TransferErrors.NotFound;
        }

        if (transfer.Status == TicketTransferStatus.Cancelled)
        {
            return Result.Success();
        }

        if (transfer.Status != TicketTransferStatus.Pending)
        {
            return TransferErrors.NotAllowed;
        }

        var ticket = await db.Tickets.SingleAsync(t => t.Id == transfer.TicketId, ct);
        transfer.Cancel(ticket);
        try
        {
            await db.SaveChangesAsync(ct);
            return Result.Success();
        }
        catch (DbUpdateConcurrencyException)
        {
            // Accepted (or expired) a moment ago.
            db.ChangeTracker.Clear();
            return TransferErrors.NotAllowed;
        }
    }
}

/// <summary><c>GET /transfers/{id}</c>: the recipient's accept page. Anyone else (another email, unconfirmed) gets 404.</summary>
public sealed class GetTransferHandler(IAppDbContext db, IUserDirectory users)
{
    public async Task<Result<TransferInvite>> HandleAsync(Guid userId, Guid transferId, CancellationToken ct) =>
        await TransferReads.ForRecipientAsync(db, users, transferId, userId, tracked: false, ct) is { } transfer
        && await TransferReads.InviteAsync(db, users, transfer, userId, ct) is { } invite
            ? invite
            : TransferErrors.NotFound;
}

/// <summary>
/// <c>POST /transfers/{id}/accept</c> (spec 14): in one transaction the transfer is accepted and the ticket changes owner (old voided,
/// new one with a new QR). Only the recipient (403-as-404); accepting again as the recipient answers the same result.
/// </summary>
public sealed class AcceptTransferHandler(IAppDbContext db, IUserDirectory users, TimeProvider time)
{
    public async Task<Result<TransferInvite>> HandleAsync(Guid userId, Guid transferId, CancellationToken ct)
    {
        var transfer = await TransferReads.ForRecipientAsync(db, users, transferId, userId, tracked: true, ct);
        if (transfer is null)
        {
            return TransferErrors.NotFound;
        }

        if (transfer.Status == TicketTransferStatus.Accepted && transfer.ToUserId == userId)
        {
            return await InviteAsync(transfer, userId, ct);
        }

        var now = time.GetUtcNow();
        if (transfer.Status != TicketTransferStatus.Pending || now >= transfer.ExpiresAt || !await ResaleReads.IsOpenAsync(db, transfer.EventId, now, ct))
        {
            return TransferErrors.NotAllowed;
        }

        try
        {
            await db.InTransactionAsync(async token =>
            {
                transfer.Accept(userId, now);
                var ticket = await db.Tickets.FirstOrDefaultAsync(
                    t => t.Id == transfer.TicketId && t.OwnerUserId == transfer.FromUserId && t.Status == TicketStatus.Listed, token)
                    ?? throw new DbUpdateConcurrencyException("The transferred ticket is no longer listed.");
                await OwnershipChange.HandOverAsync(db, ticket, ticket.OrderId, userId, token);
                await db.SaveChangesAsync(token);
                return true;
            }, ct);
        }
        catch (Exception ex) when (ex is DbUpdateException or DatabaseContentionException or DomainException)
        {
            // A second accept, a cancel or the expiry sweep got there first: answer from what committed.
            db.ChangeTracker.Clear();
            var current = await db.TicketTransfers.AsNoTracking().FirstAsync(t => t.Id == transferId, ct);
            return current.Status == TicketTransferStatus.Accepted && current.ToUserId == userId
                ? await InviteAsync(current, userId, ct)
                : TransferErrors.NotAllowed;
        }

        return await InviteAsync(transfer, userId, ct);
    }

    private async Task<Result<TransferInvite>> InviteAsync(TicketTransfer transfer, Guid userId, CancellationToken ct) =>
        await TransferReads.InviteAsync(db, users, transfer, userId, ct) is { } invite ? invite : TransferErrors.NotFound;
}
