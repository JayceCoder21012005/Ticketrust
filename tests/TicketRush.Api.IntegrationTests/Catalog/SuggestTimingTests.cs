using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Application.Features.Catalog;
using TicketRush.Domain.Events;
using TicketRush.Domain.Primitives;
using TicketRush.Domain.Venues;
using TicketRush.Infrastructure.Persistence;
using TicketRush.Infrastructure.Persistence.Seed;

namespace TicketRush.Api.IntegrationTests.Catalog;

/// <summary>Wall-clock budgets: never run next to other test classes, which would compete for the same CPU and database.</summary>
[CollectionDefinition(nameof(TimingCollection), DisableParallelization = true)]
public sealed class TimingCollection;

/// <summary>UI refresh: the suggestion endpoint answers within 30 ms (server time) on a 5,000-event catalog.</summary>
[Collection(nameof(TimingCollection))]
public sealed class SuggestTimingTests(ContainersFixture containers) : IAsyncLifetime
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
    public async Task Answers_within_30_ms_on_5000_events()
    {
        await AddEventsAsync(5000);
        await using var scope = _factory.Services.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<SuggestHandler>();
        await handler.HandleAsync("son tung", Ct);

        var times = new List<double>();
        foreach (var q in Enumerable.Repeat(new[] { "son tung", "nhac hoi", "my dinh", "hanoi", "festvl" }, 4).SelectMany(x => x))
        {
            var watch = Stopwatch.StartNew();
            await handler.HandleAsync(q, Ct);
            times.Add(watch.Elapsed.TotalMilliseconds);
        }

        times.Order().ElementAt(times.Count / 2).ShouldBeLessThan(30);
    }

    private async Task AddEventsAsync(int count)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var venue = Venue.Create("Nhà hát Lớn", "1 Tràng Tiền", "Hà Nội", null, 600);
        db.Venues.Add(venue);
        var startsAt = DateTimeOffset.UtcNow.AddDays(200);
        string[] words = ["Đêm nhạc", "Lễ hội", "Hòa nhạc", "Live show", "Festival", "Workshop", "Giải chạy", "Kịch"];
        for (var i = 0; i < count; i++)
        {
            var evt = Event.CreateDraft(Guid.CreateVersion7(), $"tai-{i}", $"{words[i % words.Length]} số {i}", "concert", venue, startsAt.AddMinutes(i), startsAt.AddMinutes(i).AddHours(2));
            evt.AddTier("GA", new Money(200_000), TicketKind.GeneralAdmission, 100, "#16a34a");
            evt.Publish();
            db.Events.Add(evt);
        }

        await db.SaveChangesAsync(Ct);
    }
}
