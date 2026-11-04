using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Application.Abstractions.Catalog;
using TicketRush.Infrastructure.Persistence;

namespace TicketRush.Api.IntegrationTests.Organizer;

/// <summary>Spec 05: checklist, publish (idempotent, one outbox message), cancel, and catalog/web cache refresh.</summary>
public sealed class OrganizerPublishTests(ContainersFixture containers) : IAsyncLifetime
{
    private readonly RecordingRevalidator _revalidator = new();
    private ApiFactory _factory = null!;
    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _app = null!;
    private HttpClient _client = null!;
    private OrganizerApi _api = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _factory = new ApiFactory(await containers.CreateDatabaseAsync(), containers.RedisConnectionString);
        _app = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<IWebCacheRevalidator>(_revalidator)));
        _client = _app.CreateClient();
        _api = await OrganizerApi.SignInAsync(_factory, _client);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        await _factory.DisposeAsync();
    }

    private static Guid Id(JsonElement body) => body.GetProperty("id").GetGuid();

    private static string Version(JsonElement body) => body.GetProperty("version").GetString()!;

    /// <summary>A draft that passes every checklist rule: cover, seated tier with 6 seats, one phase opening tomorrow.</summary>
    private async Task<JsonElement> ReadyDraftAsync(string title = "Đêm nhạc xuất bản")
    {
        var evt = await _api.CreateDraftAsync(title);
        var ticket = await OrganizerApi.BodyAsync(await _api.PostAsync($"/events/{Id(evt)}/cover-upload-url", new { contentType = "image/jpeg", size = 1000 }));
        evt = await OrganizerApi.BodyAsync(await _api.PutAsync($"/events/{Id(evt)}/info", new
        {
            version = Version(evt),
            title,
            description = "Mô tả",
            lineup = new[] { "Ca sĩ" },
            coverImageUrl = ticket.GetProperty("publicUrl").GetString(),
        }));
        evt = await OrganizerApi.BodyAsync(await _api.PutAsync($"/events/{Id(evt)}/tiers", new
        {
            version = Version(evt),
            tiers = new object[] { new { name = "Ghế", price = 500_000, kind = "Seated", color = "#7c3aed" } },
        }));
        var tier = evt.GetProperty("tiers")[0].GetProperty("id").GetGuid();
        evt = await OrganizerApi.BodyAsync(await _api.PutAsync($"/events/{Id(evt)}/layout", new
        {
            version = Version(evt),
            sections = new object[] { new { code = "A", tierId = tier, rows = 2, seatsPerRow = 3, origin = new { x = 0, y = 0 } } },
        }));
        return await OrganizerApi.BodyAsync(await _api.PutAsync($"/events/{Id(evt)}/phases", new
        {
            version = Version(evt),
            phases = new object[]
            {
                new { name = "Mở bán", opensAt = DateTimeOffset.UtcNow.AddDays(1), closesAt = DateTimeOffset.UtcNow.AddDays(10), queueEnabled = true, maxTicketsPerUser = 4 },
            },
        }));
    }

    private async Task<int> OutboxCountAsync(string type, Guid eventId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.OutboxMessages.CountAsync(m => m.Type == type && m.AggregateId == eventId, Ct);
    }

    [Fact]
    public async Task Checklist_of_an_empty_draft_lists_failures_with_their_steps()
    {
        var draft = await _api.CreateDraftAsync();

        var checklist = await OrganizerApi.BodyAsync(await _api.GetAsync($"/events/{Id(draft)}/checklist"));

        checklist.GetProperty("ready").GetBoolean().ShouldBeFalse();
        checklist.GetProperty("failures").EnumerateArray().Select(f => (f.GetProperty("code").GetString(), f.GetProperty("step").GetString()))
            .ShouldBe([("publish.missing_cover", "info"), ("publish.no_tiers", "tiers"), ("publish.no_phases", "phases")]);
    }

    [Fact]
    public async Task Checklist_of_a_ready_draft_is_ready()
    {
        var evt = await ReadyDraftAsync();

        var checklist = await OrganizerApi.BodyAsync(await _api.GetAsync($"/events/{Id(evt)}/checklist"));

        checklist.GetProperty("ready").GetBoolean().ShouldBeTrue();
        checklist.GetProperty("failures").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Publish_missing_cover_lists_publish_missing_cover_with_step_info()
    {
        var draft = await _api.CreateDraftAsync();

        var response = await _api.PostAsync($"/events/{Id(draft)}/publish");

        var body = await OrganizerApi.BodyAsync(response, 422);
        body.GetProperty("code").GetString().ShouldBe("event.publish_checklist_failed");
        body.GetProperty("failures").EnumerateArray().ShouldContain(f =>
            f.GetProperty("code").GetString() == "publish.missing_cover" && f.GetProperty("step").GetString() == "info");
        (await OutboxCountAsync("EventPublished", Id(draft))).ShouldBe(0);
    }

    [Fact]
    public async Task Publish_with_overlapping_phases_fails_and_lists_phase_overlap()
    {
        var evt = await ReadyDraftAsync();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            // The aggregate refuses overlaps, so write one the way a bad import or manual fix could.
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var opens = DateTimeOffset.UtcNow.AddDays(2);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO sale_phases (id, event_id, name, opens_at, closes_at, queue_enabled, max_tickets_per_user)
                VALUES ({Guid.CreateVersion7()}, {Id(evt)}, {"Chồng giờ"}, {opens}, {opens.AddDays(1)}, {true}, {2})
                """, Ct);
        }

        var response = await _api.PostAsync($"/events/{Id(evt)}/publish");

        var body = await OrganizerApi.BodyAsync(response, 422);
        body.GetProperty("failures").EnumerateArray().Select(f => f.GetProperty("code").GetString()).ShouldBe(["event.phase_overlap"]);
    }

    [Fact]
    public async Task Checklist_lists_a_phase_closing_after_the_event_starts_on_the_phases_step()
    {
        var evt = await ReadyDraftAsync();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            // The aggregate refuses moving the start before a phase closes; write it the way an older draft could hold it.
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var startsAt = DateTimeOffset.UtcNow.AddDays(5);
            await db.Events.Where(e => e.Id == Id(evt))
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.StartsAt, startsAt).SetProperty(e => e.EndsAt, startsAt.AddHours(3)), Ct);
        }

        var checklist = await OrganizerApi.BodyAsync(await _api.GetAsync($"/events/{Id(evt)}/checklist"));

        checklist.GetProperty("ready").GetBoolean().ShouldBeFalse();
        checklist.GetProperty("failures").EnumerateArray().Select(f => (f.GetProperty("code").GetString(), f.GetProperty("step").GetString()))
            .ShouldBe([("phase.after_event_start", "phases")]);
    }

    [Fact]
    public async Task Checklist_of_a_published_event_is_ready_after_sales_open()
    {
        var evt = await ReadyDraftAsync();
        await OrganizerApi.BodyAsync(await _api.PostAsync($"/events/{Id(evt)}/publish"));
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            // Sales opened: the "first phase opens later" rule no longer holds, and nothing in the locked steps can fix it.
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Set<Domain.Events.SalePhase>().Where(p => p.EventId == Id(evt))
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.OpensAt, DateTimeOffset.UtcNow.AddHours(-1)), Ct);
        }

        var checklist = await OrganizerApi.BodyAsync(await _api.GetAsync($"/events/{Id(evt)}/checklist"));

        checklist.GetProperty("ready").GetBoolean().ShouldBeTrue();
        checklist.GetProperty("failures").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Publish_twice_returns_200_and_one_outbox_message()
    {
        var evt = await ReadyDraftAsync();

        var first = await OrganizerApi.BodyAsync(await _api.PostAsync($"/events/{Id(evt)}/publish"));
        var second = await OrganizerApi.BodyAsync(await _api.PostAsync($"/events/{Id(evt)}/publish"));

        first.GetProperty("status").GetString().ShouldBe("Published");
        second.GetProperty("status").GetString().ShouldBe("Published");
        (await OutboxCountAsync("EventPublished", Id(evt))).ShouldBe(1);
    }

    [Fact]
    public async Task Concurrent_publish_creates_exactly_one_EventPublished()
    {
        var evt = await ReadyDraftAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => _api.PostAsync($"/events/{Id(evt)}/publish")));

        foreach (var response in responses)
        {
            (await OrganizerApi.BodyAsync(response)).GetProperty("status").GetString().ShouldBe("Published");
        }

        (await OutboxCountAsync("EventPublished", Id(evt))).ShouldBe(1);
    }

    [Fact]
    public async Task Published_event_appears_in_public_catalog_immediately()
    {
        var evt = await ReadyDraftAsync("Ra mắt ngay");
        var slug = evt.GetProperty("slug").GetString();
        (await _client.GetAsync($"/api/v1/events/{slug}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await _api.PostAsync($"/events/{Id(evt)}/publish")).EnsureSuccessStatusCode();

        (await _client.GetAsync($"/api/v1/events/{slug}", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Editing_a_published_title_refreshes_the_public_detail()
    {
        var evt = await ReadyDraftAsync("Tên cũ");
        var published = await OrganizerApi.BodyAsync(await _api.PostAsync($"/events/{Id(evt)}/publish"));
        var slug = evt.GetProperty("slug").GetString();
        (await _client.GetAsync($"/api/v1/events/{slug}", Ct)).EnsureSuccessStatusCode();

        (await _api.PutAsync($"/events/{Id(evt)}/info", new
        {
            version = Version(published),
            title = "Tên mới",
            description = "Mô tả",
            lineup = new[] { "Ca sĩ" },
            coverImageUrl = published.GetProperty("coverImageUrl").GetString(),
        })).EnsureSuccessStatusCode();

        var detail = JsonDocument.Parse(await _client.GetStringAsync($"/api/v1/events/{slug}", Ct)).RootElement;
        detail.GetProperty("title").GetString().ShouldBe("Tên mới");
    }

    [Fact]
    public async Task Cancel_emits_EventCancelled_and_hides_event_from_catalog()
    {
        var evt = await ReadyDraftAsync("Sẽ bị huỷ");
        (await _api.PostAsync($"/events/{Id(evt)}/publish")).EnsureSuccessStatusCode();
        var slug = evt.GetProperty("slug").GetString();
        (await _client.GetAsync($"/api/v1/events/{slug}", Ct)).EnsureSuccessStatusCode();

        var cancelled = await OrganizerApi.BodyAsync(await _api.PostAsync($"/events/{Id(evt)}/cancel"));
        var again = await _api.PostAsync($"/events/{Id(evt)}/cancel");

        cancelled.GetProperty("status").GetString().ShouldBe("Cancelled");
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await OrganizerApi.CodeAsync(again)).ShouldBe("event.not_cancellable");
        (await OutboxCountAsync("EventCancelled", Id(evt))).ShouldBe(1);
        (await _client.GetAsync($"/api/v1/events/{slug}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Outbox_delivery_revalidates_the_web_cache_tags_of_the_event()
    {
        var evt = await ReadyDraftAsync("Làm mới web");
        (await _api.PostAsync($"/events/{Id(evt)}/publish")).EnsureSuccessStatusCode();

        var processor = _app.Services.GetRequiredService<TicketRush.Infrastructure.Outbox.OutboxProcessor>();
        while (await processor.ProcessBatchAsync(Ct) > 0)
        {
        }

        _revalidator.Calls.ShouldContain(tags =>
            tags.SequenceEqual(new[] { "events:list", $"event:{Id(evt)}", $"event-slug:{evt.GetProperty("slug").GetString()}" }));
    }

    private sealed class RecordingRevalidator : IWebCacheRevalidator
    {
        public ConcurrentQueue<IReadOnlyList<string>> Calls { get; } = new();

        public Task RevalidateAsync(IReadOnlyList<string> tags, CancellationToken ct)
        {
            Calls.Enqueue(tags);
            return Task.CompletedTask;
        }
    }
}
