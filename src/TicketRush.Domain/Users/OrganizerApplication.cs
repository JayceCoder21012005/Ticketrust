using TicketRush.Domain.Primitives;

namespace TicketRush.Domain.Users;

public enum OrganizerApplicationStatus
{
    Pending,
    Approved,
    Rejected,
}

/// <summary>The admin decided an application (spec 16): email the applicant.</summary>
public sealed record OrganizerApplicationDecided(Guid ApplicationId, Guid UserId, OrganizerApplicationStatus Decision, string Reason) : IDomainEvent;

/// <summary>
/// A user asking to sell tickets (spec 16). <c>Pending → Approved</c> (the admin grants the Organizer role) or <c>Rejected</c> with a
/// reason. One pending application per user (a unique partial index); a rejected user may apply again.
/// </summary>
public sealed class OrganizerApplication : AggregateRoot, IAuditable
{
    public const int CompanyNameMaxLength = 200;
    public const int DescriptionMaxLength = 2000;
    public const int ReasonMaxLength = 500;

    private OrganizerApplication(Guid userId, string companyName, string phone, string description)
        : base(Guid.CreateVersion7())
    {
        UserId = userId;
        CompanyName = companyName;
        Phone = phone;
        Description = description;
        Status = OrganizerApplicationStatus.Pending;
    }

    // Required by EF Core.
    private OrganizerApplication()
    {
        CompanyName = string.Empty;
        Phone = string.Empty;
        Description = string.Empty;
    }

    public Guid UserId { get; private set; }

    public string CompanyName { get; private set; }

    public string Phone { get; private set; }

    public string Description { get; private set; }

    public OrganizerApplicationStatus Status { get; private set; }

    public string? DecisionReason { get; private set; }

    public Guid? ReviewedByUserId { get; private set; }

    public DateTimeOffset? ReviewedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static OrganizerApplication Submit(Guid userId, string companyName, string phone, string description)
    {
        if (userId == Guid.Empty || string.IsNullOrWhiteSpace(companyName) || string.IsNullOrWhiteSpace(phone))
        {
            throw new DomainException("organizer_application.invalid", "An application needs a user, a company name and a phone number.");
        }

        return new OrganizerApplication(userId, Clip(companyName, CompanyNameMaxLength), phone.Trim(), Clip(description ?? string.Empty, DescriptionMaxLength));
    }

    public void Approve(Guid adminId, string reason, DateTimeOffset now) => Decide(OrganizerApplicationStatus.Approved, adminId, reason, now);

    public void Reject(Guid adminId, string reason, DateTimeOffset now) => Decide(OrganizerApplicationStatus.Rejected, adminId, reason, now);

    private void Decide(OrganizerApplicationStatus decision, Guid adminId, string reason, DateTimeOffset now)
    {
        if (Status != OrganizerApplicationStatus.Pending)
        {
            throw new DomainException("organizer_application.decided", $"This application was already {Status}.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainException("admin.reason_required", "A decision needs a reason.");
        }

        Status = decision;
        DecisionReason = Clip(reason, ReasonMaxLength);
        ReviewedByUserId = adminId;
        ReviewedAt = now;
        Raise(new OrganizerApplicationDecided(Id, UserId, decision, DecisionReason));
    }

    private static string Clip(string value, int max)
    {
        var trimmed = value.Trim();
        return trimmed.Length > max ? trimmed[..max] : trimmed;
    }
}
