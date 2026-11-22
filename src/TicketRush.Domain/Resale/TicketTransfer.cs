using TicketRush.Domain.Primitives;
using TicketRush.Domain.Tickets;

namespace TicketRush.Domain.Resale;

public enum TicketTransferStatus
{
    Pending,
    Accepted,
    Cancelled,
    Expired,
}

/// <summary>
/// A ticket offered to a friend by email (spec 14). <c>Pending → Accepted | Cancelled | Expired</c> (after
/// <see cref="ResaleRules.TransferTtl"/>). While pending the ticket is <see cref="TicketStatus.Listed"/>.
/// </summary>
public sealed class TicketTransfer : AggregateRoot, IAuditable
{
    private TicketTransfer(Ticket ticket, string toEmail, DateTimeOffset now)
        : base(Guid.CreateVersion7())
    {
        TicketId = ticket.Id;
        EventId = ticket.EventId;
        FromUserId = ticket.OwnerUserId;
        ToEmail = toEmail;
        Status = TicketTransferStatus.Pending;
        ExpiresAt = now + ResaleRules.TransferTtl;
    }

    // Required by EF Core.
    private TicketTransfer()
    {
        ToEmail = string.Empty;
    }

    public Guid TicketId { get; private set; }

    public Guid EventId { get; private set; }

    public Guid FromUserId { get; private set; }

    /// <summary>Trimmed and lower-cased.</summary>
    public string ToEmail { get; private set; }

    /// <summary>Who accepted it.</summary>
    public Guid? ToUserId { get; private set; }

    public TicketTransferStatus Status { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    /// <summary>Lists <paramref name="ticket"/> (Valid → Listed) for <paramref name="toEmail"/>; <paramref name="fromEmail"/> is the owner's.</summary>
    public static TicketTransfer Create(Ticket ticket, string fromEmail, string toEmail, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentException.ThrowIfNullOrWhiteSpace(toEmail);
        ArgumentException.ThrowIfNullOrWhiteSpace(fromEmail);
        var to = NormalizeEmail(toEmail);
        if (to == NormalizeEmail(fromEmail))
        {
            throw new DomainException("transfer.self", "You cannot transfer a ticket to yourself.");
        }

        if (ticket.Status != TicketStatus.Valid)
        {
            throw new DomainException("transfer.not_allowed", $"A ticket that is {ticket.Status} cannot be transferred.");
        }

        ticket.List();
        var transfer = new TicketTransfer(ticket, to, now);
        transfer.Raise(new TransferCreated(transfer.Id, transfer.TicketId, transfer.EventId, transfer.FromUserId, to, transfer.ExpiresAt));
        return transfer;
    }

    /// <summary>Pending and not expired → Accepted. The caller changes the ticket's owner in the same transaction.</summary>
    public void Accept(Guid userId, DateTimeOffset now)
    {
        if (Status != TicketTransferStatus.Pending || now >= ExpiresAt)
        {
            throw NotAllowed();
        }

        ToUserId = userId;
        Status = TicketTransferStatus.Accepted;
    }

    /// <summary>The sender takes it back: the ticket is theirs again.</summary>
    public void Cancel(Ticket ticket) => End(ticket, TicketTransferStatus.Cancelled);

    /// <summary>Nobody accepted it in time (or the cutoff came): the ticket is the sender's again.</summary>
    public void Expire(Ticket ticket) => End(ticket, TicketTransferStatus.Expired);

    /// <summary>The event was cancelled: its tickets are voided by the cancellation itself.</summary>
    public void CloseForCancelledEvent()
    {
        if (Status == TicketTransferStatus.Pending)
        {
            Status = TicketTransferStatus.Cancelled;
        }
    }

    private void End(Ticket ticket, TicketTransferStatus to)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        if (Status != TicketTransferStatus.Pending)
        {
            throw NotAllowed();
        }

        if (ticket.Id != TicketId)
        {
            throw new DomainException("transfer.invalid_reference", "That is not the transferred ticket.");
        }

        ticket.Unlist();
        Status = to;
    }

    private DomainException NotAllowed() => new("transfer.not_allowed", $"A transfer that is {Status} cannot change.");
}
