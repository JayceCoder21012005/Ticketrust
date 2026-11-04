using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Application.Abstractions.Queue;
using TicketRush.Domain.Events;
using TicketRush.Infrastructure.Persistence;
using TicketRush.Infrastructure.Persistence.Seed;

namespace TicketRush.Api.IntegrationTests.Queue;

public sealed class QueuePhaseSourceTests(ContainersFixture containers) : IAsyncLifetime
{
    private ApiFactory _factory = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _factory = new ApiFactory(await containers.CreateDatabaseAsync(), containers.RedisConnectionString);
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DevDataSeeder>().SeedAsync(Ct);
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Lists_only_queued_phases_of_published_events_inside_the_lookahead()
    {
        var source = _factory.Services.GetRequiredService<IQueuePhaseSource>();
        var now = DateTimeOffset.UtcNow;

        var live = await source.ListLiveAsync(now, TimeSpan.FromMinutes(30), Ct);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var openingSoon = await db.Events.Where(e => e.Slug == SeedEventCatalog.OpeningSoonSlug).SelectMany(e => e.Phases).SingleAsync(Ct);
        var presale = await db.Events.Where(e => e.Slug == "den-vau-show-2026").SelectMany(e => e.Phases).ToListAsync(Ct);
        var notQueued = await db.Events.SelectMany(e => e.Phases).Where(p => !p.QueueEnabled).Select(p => p.Id).ToListAsync(Ct);

        live.ShouldContain(s => s.PhaseId == openingSoon.Id && s.EventId == openingSoon.EventId && s.QueueEnabled);
        live.ShouldAllBe(s => s.QueueEnabled && s.OpensAt <= now.AddMinutes(30));
        live.Select(s => s.PhaseId).ShouldNotContain(id => presale.Any(p => p.Id == id));
        live.Select(s => s.PhaseId).ShouldNotContain(id => notQueued.Contains(id));
    }

    [Fact]
    public async Task Drops_phases_of_cancelled_events()
    {
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var evt = await db.Events.Include(e => e.Phases).SingleAsync(e => e.Slug == SeedEventCatalog.OpeningSoonSlug, Ct);
            evt.Cancel();
            await db.SaveChangesAsync(Ct);
        }

        var live = await _factory.Services.GetRequiredService<IQueuePhaseSource>()
            .ListLiveAsync(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(30), Ct);

        await using var check = _factory.Services.CreateAsyncScope();
        var cancelledPhase = await check.ServiceProvider.GetRequiredService<AppDbContext>().Events
            .Where(e => e.Status == EventStatus.Cancelled).SelectMany(e => e.Phases).Select(p => p.Id).ToListAsync(Ct);
        live.Select(s => s.PhaseId).ShouldNotContain(id => cancelledPhase.Contains(id));
    }
}
