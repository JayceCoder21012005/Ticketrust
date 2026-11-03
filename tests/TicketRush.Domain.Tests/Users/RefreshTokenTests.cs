using TicketRush.Domain.Primitives;
using TicketRush.Domain.Users;

namespace TicketRush.Domain.Tests.Users;

public sealed class RefreshTokenTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid UserId = Guid.CreateVersion7();

    private static RefreshToken Issue() => RefreshToken.Issue(UserId, "hash-1", Now, "10.0.0.1", "test-agent");

    [Fact]
    public void Issue_starts_a_new_family_with_14_day_sliding_and_30_day_absolute_expiry()
    {
        var token = Issue();

        token.UserId.ShouldBe(UserId);
        token.TokenHash.ShouldBe("hash-1");
        token.FamilyId.ShouldNotBe(Guid.Empty);
        token.ExpiresAt.ShouldBe(Now.AddDays(14));
        token.FamilyExpiresAt.ShouldBe(Now.AddDays(30));
        token.CreatedByIp.ShouldBe("10.0.0.1");
        token.UserAgent.ShouldBe("test-agent");
        token.IsActive(Now).ShouldBeTrue();
    }

    [Fact]
    public void Two_issued_tokens_belong_to_different_families()
    {
        Issue().FamilyId.ShouldNotBe(Issue().FamilyId);
    }

    [Fact]
    public void Rotate_revokes_the_old_token_and_links_it_to_a_new_one_in_the_same_family()
    {
        var old = Issue();
        var later = Now.AddDays(1);

        var next = old.Rotate("hash-2", later, "10.0.0.2", "agent-2");

        old.RevokedAt.ShouldBe(later);
        old.ReplacedById.ShouldBe(next.Id);
        old.IsActive(later).ShouldBeFalse();
        next.FamilyId.ShouldBe(old.FamilyId);
        next.UserId.ShouldBe(UserId);
        next.TokenHash.ShouldBe("hash-2");
        next.ExpiresAt.ShouldBe(later.AddDays(14));
        next.FamilyExpiresAt.ShouldBe(old.FamilyExpiresAt);
        next.IsActive(later).ShouldBeTrue();
    }

    [Fact]
    public void Sliding_expiry_never_passes_the_family_absolute_expiry()
    {
        var token = Issue();
        var day10 = Now.AddDays(10);
        token = token.Rotate("hash-2", day10, null, null);
        var day20 = Now.AddDays(20);

        var next = token.Rotate("hash-3", day20, null, null);

        next.ExpiresAt.ShouldBe(Now.AddDays(30));
    }

    [Fact]
    public void Token_is_inactive_once_it_expires()
    {
        var token = Issue();

        token.IsActive(Now.AddDays(14).AddTicks(-1)).ShouldBeTrue();
        token.IsActive(Now.AddDays(14)).ShouldBeFalse();
    }

    [Fact]
    public void Rotating_a_revoked_token_is_rejected()
    {
        var token = Issue();
        token.Rotate("hash-2", Now, null, null);

        Should.Throw<DomainException>(() => token.Rotate("hash-3", Now, null, null)).Code.ShouldBe("auth.refresh_invalid");
    }

    [Fact]
    public void Rotating_an_expired_token_is_rejected()
    {
        var token = Issue();

        Should.Throw<DomainException>(() => token.Rotate("hash-2", Now.AddDays(15), null, null)).Code.ShouldBe("auth.refresh_invalid");
    }

    [Fact]
    public void Revoke_keeps_the_first_revocation_time()
    {
        var token = Issue();

        token.Revoke(Now.AddHours(1));
        token.Revoke(Now.AddHours(2));

        token.IsRevoked.ShouldBeTrue();
        token.RevokedAt.ShouldBe(Now.AddHours(1));
    }
}
