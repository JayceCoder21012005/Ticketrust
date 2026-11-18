using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TicketRush.Api.IntegrationTests.Auth;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Application.Abstractions.Security;
using TicketRush.Domain.Users;
using TicketRush.Infrastructure.Identity;
using TicketRush.Infrastructure.Persistence;
using TicketRush.Infrastructure.Security;

namespace TicketRush.Api.IntegrationTests.Security;

/// <summary>Spec 12 velocity flags against real Redis and Postgres: flagged for review, never blocked, one open flag per reason.</summary>
public sealed class SuspiciousActivityDetectorTests(ContainersFixture containers) : IAsyncLifetime
{
    private ApiFactory _factory = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() =>
        _factory = new ApiFactory(
            await containers.CreateDatabaseAsync(),
            containers.RedisConnectionString,
            settings: new Dictionary<string, string?> { ["Redis:Environment"] = $"sa{Guid.NewGuid():N}"[..12] });

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Theory]
    [InlineData("203.0.113.77", "203.0.113.0/24")]
    [InlineData("::ffff:198.51.100.9", "198.51.100.0/24")]
    [InlineData("2001:db8:abcd:12:1:2:3:4", "2001:db8:abcd::/48")]
    [InlineData("not-an-ip", null)]
    [InlineData(null, null)]
    public void Subnets_are_the_slash_24_or_slash_48(string? ip, string? subnet) =>
        SuspiciousActivityDetector.Subnet(ip).ShouldBe(subnet);

    [Fact]
    public async Task More_than_twenty_joins_a_minute_from_one_subnet_flags_the_joiners_after_the_twentieth_once()
    {
        var users = Enumerable.Range(0, 22).Select(_ => Guid.CreateVersion7()).ToList();

        // The window is the wall-clock minute: start early in one so all joins land in it.
        var second = DateTimeOffset.UtcNow.Second;
        if (second > 45)
        {
            await Task.Delay(TimeSpan.FromSeconds(61 - second), Ct);
        }

        for (var i = 0; i < users.Count; i++)
        {
            await WithDetectorAsync(d => d.QueueJoinedAsync(users[i], $"203.0.113.{i + 1}", Ct));
        }

        await WithDetectorAsync(d => d.QueueJoinedAsync(users[^1], "203.0.113.200", Ct));
        await WithDetectorAsync(d => d.QueueJoinedAsync(Guid.CreateVersion7(), "198.51.100.1", Ct));

        var flags = await FlagsAsync();
        flags.Select(f => f.UserId).ShouldBe([users[20], users[21]], ignoreOrder: true);
        flags.ShouldAllBe(f => f.Reason == AccountFlag.SubnetJoinBurst && f.Detail.Contains("203.0.113.0/24"));
    }

    [Fact]
    public async Task A_phone_number_on_more_than_three_accounts_flags_every_holder_in_either_spelling()
    {
        var holders = new List<Guid>();
        foreach (var phone in new[] { "0912345678", "+84912345678", "0912345678" })
        {
            holders.Add(await CreateUserAsync(phone));
        }

        await WithDetectorAsync(d => d.PhoneNumberSavedAsync(holders[^1], "0912345678", Ct));
        (await FlagsAsync()).ShouldBeEmpty("three accounts may share a family phone");

        holders.Add(await CreateUserAsync("+84912345678"));
        await WithDetectorAsync(d => d.PhoneNumberSavedAsync(holders[^1], "+84912345678", Ct));
        await WithDetectorAsync(d => d.PhoneNumberSavedAsync(holders[^1], "+84912345678", Ct));

        var flags = await FlagsAsync();
        flags.Select(f => f.UserId).ShouldBe(holders, ignoreOrder: true);
        flags.ShouldAllBe(f => f.Reason == AccountFlag.SharedPhone && !f.Detail.Contains("912345678"));
    }

    private async Task WithDetectorAsync(Func<ISuspiciousActivityDetector, Task> action)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<ISuspiciousActivityDetector>());
    }

    private async Task<List<AccountFlag>> FlagsAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Set<AccountFlag>().AsNoTracking().ToListAsync(Ct);
    }

    private async Task<Guid> CreateUserAsync(string phone)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var email = $"u{Guid.NewGuid():N}@example.com";
        var user = new AppUser { Id = Guid.CreateVersion7(), Email = email, UserName = email, FullName = "Fan", PhoneNumber = phone, EmailConfirmed = true };
        (await users.CreateAsync(user, AuthApi.Password)).Succeeded.ShouldBeTrue();
        return user.Id;
    }
}
