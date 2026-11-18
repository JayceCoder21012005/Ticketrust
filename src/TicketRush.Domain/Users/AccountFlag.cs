using TicketRush.Domain.Primitives;

namespace TicketRush.Domain.Users;

/// <summary>
/// A sign that an account may belong to a bot or a scalper (spec 12 velocity flags), for an admin to review (spec 16). A flag
/// never blocks anything by itself: real fans share phones and networks too. One open flag per (user, reason).
/// </summary>
public sealed class AccountFlag : Entity
{
    public const int DetailMaxLength = 200;

    /// <summary>The account's phone number is on more than 3 accounts.</summary>
    public const string SharedPhone = "shared_phone";

    /// <summary>More than 20 queue joins in one minute came from the account's /24 subnet.</summary>
    public const string SubnetJoinBurst = "subnet_join_burst";

    private AccountFlag(Guid userId, string reason, string detail, DateTimeOffset now)
        : base(Guid.CreateVersion7())
    {
        UserId = userId;
        Reason = reason;
        Detail = detail;
        CreatedAt = now;
    }

    // Required by EF Core.
    private AccountFlag()
    {
    }

    public Guid UserId { get; private set; }

    public string Reason { get; private set; } = string.Empty;

    /// <summary>What was seen, without personal data (a count, a subnet; never a full phone number).</summary>
    public string Detail { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Set by the admin review (spec 16); an open flag has none.</summary>
    public DateTimeOffset? ResolvedAt { get; private set; }

    public static AccountFlag Raise(Guid userId, string reason, string detail, DateTimeOffset now)
    {
        if (userId == Guid.Empty || reason is not (SharedPhone or SubnetJoinBurst))
        {
            throw new DomainException("account_flag.invalid", "A flag needs an account and a known reason.");
        }

        return new AccountFlag(userId, reason, detail.Length > DetailMaxLength ? detail[..DetailMaxLength] : detail, now);
    }
}
