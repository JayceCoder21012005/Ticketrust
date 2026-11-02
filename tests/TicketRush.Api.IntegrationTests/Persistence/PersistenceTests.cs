using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Domain.Events;
using TicketRush.Infrastructure;
using TicketRush.Infrastructure.Identity;
using TicketRush.Infrastructure.Persistence;
using TicketRush.Infrastructure.Persistence.Seed;

namespace TicketRush.Api.IntegrationTests.Persistence;

public sealed class PersistenceTests(ContainersFixture containers) : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(TestSettings.For(await containers.CreateDatabaseAsync(), containers.RedisConnectionString));
        builder.AddPersistence();
        _host = builder.Build();
    }

    public ValueTask DisposeAsync()
    {
        _host.Dispose();
        return ValueTask.CompletedTask;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AsyncServiceScope Scope() => _host.Services.CreateAsyncScope();

    [Fact]
    public async Task Seed_runs_twice_without_duplicates()
    {
        for (var run = 0; run < 2; run++)
        {
            await using var scope = Scope();
            await scope.ServiceProvider.GetRequiredService<DevDataSeeder>().SeedAsync(Ct);
        }

        await using var check = Scope();
        var db = check.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.Users.CountAsync(Ct)).ShouldBe(4);
        (await db.Venues.CountAsync(Ct)).ShouldBe(2);
        (await db.Events.CountAsync(Ct)).ShouldBe(6);
        (await db.Events.Select(e => e.Status).Distinct().CountAsync(Ct)).ShouldBe(Enum.GetValues<EventStatus>().Length);
        var openingSoon = await db.Events.Include(e => e.Phases).SingleAsync(e => e.Slug == "ha-anh-tuan-chan-troi-ruc-ro", Ct);
        openingSoon.Phases.ShouldHaveSingleItem().OpensAt.ShouldBe(DateTimeOffset.UtcNow.AddMinutes(5), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Seeded_users_can_sign_in_with_the_dev_password_and_have_their_role()
    {
        await using (var seed = Scope())
        {
            await seed.ServiceProvider.GetRequiredService<DevDataSeeder>().SeedAsync(Ct);
        }

        await using var scope = Scope();
        var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<AppUser>>();
        var organizer = (await users.FindByEmailAsync("organizer@ticketrush.dev"))!;

        (await users.CheckPasswordAsync(organizer, DevDataSeeder.DevPassword)).ShouldBeTrue();
        (await users.IsInRoleAsync(organizer, "Organizer")).ShouldBeTrue();
    }

    [Fact]
    public async Task Domain_events_become_outbox_rows_in_the_same_save()
    {
        await using var scope = Scope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var evt = await NewPublishedEventAsync(db, "outbox-same-save");

        var message = await db.OutboxMessages.AsNoTracking().SingleAsync(Ct);
        message.Type.ShouldBe(nameof(EventPublished));
        message.AggregateId.ShouldBe(evt.Id);
        message.Payload.ShouldContain(evt.Id.ToString());
        evt.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_failed_save_writes_no_outbox_row()
    {
        await using (var first = Scope())
        {
            await NewPublishedEventAsync(first.ServiceProvider.GetRequiredService<AppDbContext>(), "duplicate-slug");
        }

        await using var scope = Scope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await Should.ThrowAsync<DbUpdateException>(() => NewPublishedEventAsync(db, "duplicate-slug"));

        await using var check = Scope();
        (await check.ServiceProvider.GetRequiredService<AppDbContext>().OutboxMessages.CountAsync(Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Concurrent_updates_to_one_aggregate_are_rejected_by_xmin()
    {
        Guid id;
        await using (var setup = Scope())
        {
            id = (await NewPublishedEventAsync(setup.ServiceProvider.GetRequiredService<AppDbContext>(), "concurrency")).Id;
        }

        await using var scopeA = Scope();
        await using var scopeB = Scope();
        var dbA = scopeA.ServiceProvider.GetRequiredService<AppDbContext>();
        var dbB = scopeB.ServiceProvider.GetRequiredService<AppDbContext>();
        var a = await dbA.Events.SingleAsync(e => e.Id == id, Ct);
        var b = await dbB.Events.SingleAsync(e => e.Id == id, Ct);

        a.MarkSoldOut(true);
        await dbA.SaveChangesAsync(Ct);
        b.Cancel();

        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => dbB.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task Audit_timestamps_are_set_on_insert_and_update()
    {
        await using var scope = Scope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var evt = await NewPublishedEventAsync(db, "audit");
        var created = evt.CreatedAt;

        evt.MarkSoldOut(true);
        await db.SaveChangesAsync(Ct);

        created.ShouldBeGreaterThan(DateTimeOffset.MinValue);
        evt.UpdatedAt.ShouldBeGreaterThan(created);
    }

    private static async Task<Event> NewPublishedEventAsync(AppDbContext db, string slug)
    {
        var venue = Domain.Venues.Venue.Create("Venue", "Address", "Hà Nội", null, 100);
        var now = DateTimeOffset.UtcNow;
        var evt = Event.CreateDraft(Guid.CreateVersion7(), slug, "Title", "concert", venue.Id, now.AddDays(10), now.AddDays(10).AddHours(2));
        evt.Publish();
        db.Venues.Add(venue);
        db.Events.Add(evt);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return evt;
    }
}
