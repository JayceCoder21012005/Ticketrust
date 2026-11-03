using TicketRush.Domain.Primitives;

namespace TicketRush.Domain.Users;

/// <summary>
/// One refresh token of a login session ("family"). Only the SHA-256 hash of the raw token is stored.
/// Every refresh rotates the token; presenting a revoked token again means it was stolen (spec 03).
/// An aggregate so it gets the <c>xmin</c> concurrency token: two parallel rotations of one token can't both win.
/// </summary>
public sealed class RefreshToken : AggregateRoot
{
    public static readonly TimeSpan SlidingLifetime = TimeSpan.FromDays(14);
    public static readonly TimeSpan AbsoluteLifetime = TimeSpan.FromDays(30);

    private RefreshToken(
        Guid userId,
        string tokenHash,
        Guid familyId,
        DateTimeOffset now,
        DateTimeOffset familyExpiresAt,
        string? createdByIp,
        string? userAgent)
        : base(Guid.CreateVersion7())
    {
        UserId = userId;
        TokenHash = tokenHash;
        FamilyId = familyId;
        CreatedAt = now;
        FamilyExpiresAt = familyExpiresAt;
        ExpiresAt = Min(now + SlidingLifetime, familyExpiresAt);
        CreatedByIp = createdByIp;
        UserAgent = userAgent;
    }

    // Required by EF Core.
    private RefreshToken()
    {
        TokenHash = string.Empty;
    }

    public Guid UserId { get; private set; }

    public string TokenHash { get; private set; }

    public Guid FamilyId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    /// <summary>30 days after the login that started the family; rotation never extends it.</summary>
    public DateTimeOffset FamilyExpiresAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public Guid? ReplacedById { get; private set; }

    public string? CreatedByIp { get; private set; }

    public string? UserAgent { get; private set; }

    public bool IsRevoked => RevokedAt is not null;

    public static RefreshToken Issue(Guid userId, string tokenHash, DateTimeOffset now, string? createdByIp, string? userAgent) =>
        new(userId, tokenHash, Guid.CreateVersion7(), now, now + AbsoluteLifetime, createdByIp, userAgent);

    public bool IsActive(DateTimeOffset now) => !IsRevoked && now < ExpiresAt;

    public RefreshToken Rotate(string newTokenHash, DateTimeOffset now, string? createdByIp, string? userAgent)
    {
        if (!IsActive(now))
        {
            throw new DomainException(AuthErrors.RefreshInvalid.Code, "Only an active refresh token can be rotated.");
        }

        var next = new RefreshToken(UserId, newTokenHash, FamilyId, now, FamilyExpiresAt, createdByIp, userAgent);
        RevokedAt = now;
        ReplacedById = next.Id;
        return next;
    }

    public void Revoke(DateTimeOffset now) => RevokedAt ??= now;

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}
