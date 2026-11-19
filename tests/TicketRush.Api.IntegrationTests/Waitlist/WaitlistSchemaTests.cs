using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Domain.Waitlist;
using TicketRush.Infrastructure;
using TicketRush.Infrastructure.Persistence;

namespace TicketRush.Api.IntegrationTests.Waitlist;

/// <summary>Spec 13 persistence: one live entry per (event, user); a finished entry does not block joining again.</summary>
public sealed class WaitlistSchemaTests(ContainersFixture containers) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 3, 0, 0, TimeSpan.Zero);
    private IHost _host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(TestSettings.For(await containers.CreateDatabaseAsync(), containers.RedisConnectionString));
        builder.AddPersistence();
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        _host = builder.Build();
    }

    public ValueTask DisposeAsync()
    {
        _host.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task SaveAsync(WaitlistEntry entry)
    {
        await using var scope = _host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Add(entry);
        await db.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task The_migrated_schema_matches_the_model()
    {
        await using var scope = _host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Database.HasPendingModelChanges().ShouldBeFalse();
        (await db.Database.GetPendingMigrationsAsync(Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_second_live_entry_for_the_same_event_and_user_is_a_unique_violation()
    {
        var (eventId, userId) = (Guid.NewGuid(), Guid.NewGuid());
        await SaveAsync(WaitlistEntry.Join(eventId, userId, null, 1, Now));

        var exception = await Should.ThrowAsync<DbUpdateException>(() => SaveAsync(WaitlistEntry.Join(eventId, userId, null, 2, Now)));
        exception.InnerException.ShouldBeOfType<PostgresException>().SqlState.ShouldBe("23505");
    }

    [Fact]
    public async Task A_user_who_left_may_join_again()
    {
        var (eventId, userId) = (Guid.NewGuid(), Guid.NewGuid());
        var left = WaitlistEntry.Join(eventId, userId, null, 1, Now);
        left.Leave();
        await SaveAsync(left);

        await SaveAsync(WaitlistEntry.Join(eventId, userId, null, 1, Now));
    }
}
