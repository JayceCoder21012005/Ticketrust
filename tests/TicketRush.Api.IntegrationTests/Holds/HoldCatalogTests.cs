using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Application.Abstractions.Inventory;
using TicketRush.Domain.Events;
using TicketRush.Infrastructure.Persistence;
using TicketRush.Infrastructure.Persistence.Seed;

namespace TicketRush.Api.IntegrationTests.Holds;

public sealed class HoldCatalogTests(ContainersFixture containers) : IAsyncLifetime
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

    private async Task<Guid> EventIdAsync(string slug)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Events.Where(e => e.Slug == slug).Select(e => e.Id).SingleAsync(Ct);
    }

    private async Task<HoldCatalogEntry?> GetAsync(Guid eventId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IHoldCatalog>().GetAsync(eventId, Ct);
    }

    [Fact]
    public async Task Son_tung_has_600_seats_with_tiers_and_its_phase()
    {
        var entry = (await GetAsync(await EventIdAsync("son-tung-mtp-live"))).ShouldNotBeNull();

        entry.Slug.ShouldBe("son-tung-mtp-live");
        entry.Seats.Count.ShouldBe(600);
        entry.Seats.Select(s => s.Index).ShouldBe(Enumerable.Range(0, 600));
        entry.Tiers.Count(t => t.Kind == TicketKind.Seated).ShouldBe(2);
        entry.Tiers.ShouldAllBe(t => t.Price > 0);
        entry.Phases.ShouldNotBeEmpty();
        entry.Phases.ShouldAllBe(p => p.MaxTicketsPerUser >= 1);
    }

    [Fact]
    public async Task Unknown_events_return_null()
    {
        (await GetAsync(Guid.NewGuid())).ShouldBeNull();
    }

    [Fact]
    public async Task A_second_read_runs_no_query()
    {
        var id = await EventIdAsync("son-tung-mtp-live");
        await GetAsync(id);
        _factory.Queries.Reset();

        await GetAsync(id);

        _factory.Queries.Matching(sql => sql.Contains("FROM events", StringComparison.Ordinal) || sql.Contains("event_seats", StringComparison.Ordinal)).ShouldBeEmpty();
    }
}
