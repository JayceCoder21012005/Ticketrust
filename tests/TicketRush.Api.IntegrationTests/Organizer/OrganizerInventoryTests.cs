using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Application.Abstractions.Security;
using TicketRush.Domain.Events;
using TicketRush.Infrastructure.Persistence;

namespace TicketRush.Api.IntegrationTests.Organizer;

/// <summary>Spec 05: tiers, generated layout and sale phases (all draft-only).</summary>
public sealed class OrganizerInventoryTests(ContainersFixture containers) : IAsyncLifetime
{
    private const string Violet = "#7c3aed";
    private const string Teal = "#0f766e";

    private ApiFactory _factory = null!;
    private HttpClient _client = null!;
    private OrganizerApi _api = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _factory = new ApiFactory(await containers.CreateDatabaseAsync(), containers.RedisConnectionString);
        _client = _factory.CreateClient();
        _api = await OrganizerApi.SignInAsync(_factory, _client);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private static Guid Id(JsonElement body) => body.GetProperty("id").GetGuid();

    private static string Version(JsonElement body) => body.GetProperty("version").GetString()!;

    /// <summary>Draft with a seated tier (first) and a standing tier (second).</summary>
    private async Task<JsonElement> DraftWithTiersAsync()
    {
        var draft = await _api.CreateDraftAsync();
        var response = await _api.PutAsync($"/events/{Id(draft)}/tiers", new
        {
            version = Version(draft),
            tiers = new object[]
            {
                new { name = "Ghế ngồi", price = 800_000, kind = "Seated", color = Violet },
                new { name = "Khu đứng", price = 500_000, kind = "GeneralAdmission", capacity = 300, color = Teal },
            },
        });
        return await OrganizerApi.BodyAsync(response);
    }

    private static Guid TierId(JsonElement evt, int index) => evt.GetProperty("tiers")[index].GetProperty("id").GetGuid();

    private Task<HttpResponseMessage> PutLayoutAsync(JsonElement evt, params object[] sections) =>
        _api.PutAsync($"/events/{Id(evt)}/layout", new { version = Version(evt), sections });

    private Task<HttpResponseMessage> PutPhasesAsync(JsonElement evt, params object[] phases) =>
        _api.PutAsync($"/events/{Id(evt)}/phases", new { version = Version(evt), phases });

    private static object Phase(int opensInDays, int closesInDays, object? accessCode = null, Guid? id = null) => new
    {
        id,
        name = "Mở bán",
        opensAt = DateTimeOffset.UtcNow.AddDays(opensInDays),
        closesAt = DateTimeOffset.UtcNow.AddDays(closesInDays),
        queueEnabled = true,
        maxTicketsPerUser = 4,
        accessCode,
    };

    [Fact]
    public async Task Tiers_are_replaced_and_keep_their_ids()
    {
        var evt = await DraftWithTiersAsync();
        var seated = TierId(evt, 0);

        var response = await _api.PutAsync($"/events/{Id(evt)}/tiers", new
        {
            version = Version(evt),
            tiers = new object[] { new { id = seated, name = "Ghế VIP", price = 1_200_000, kind = "Seated", color = Violet } },
        });

        var updated = await OrganizerApi.BodyAsync(response);
        updated.GetProperty("tiers").GetArrayLength().ShouldBe(1);
        TierId(updated, 0).ShouldBe(seated);
        updated.GetProperty("tiers")[0].GetProperty("price").GetInt64().ShouldBe(1_200_000);
    }

    [Theory]
    [InlineData(0, Violet, null)]
    [InlineData(800_000, "#ffffff", null)]
    [InlineData(800_000, Violet, 0)]
    public async Task Invalid_tiers_are_a_validation_problem(long price, string color, int? capacity)
    {
        var draft = await _api.CreateDraftAsync();

        var response = await _api.PutAsync($"/events/{Id(draft)}/tiers", new
        {
            version = Version(draft),
            tiers = new object[] { new { name = "Đứng", price, kind = "GeneralAdmission", capacity = capacity ?? 100, color } },
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Layout_20x30_generates_600_contiguous_seats()
    {
        var evt = await DraftWithTiersAsync();

        var response = await PutLayoutAsync(evt, new { code = "A", tierId = TierId(evt, 0), rows = 20, seatsPerRow = 30, origin = new { x = 100, y = 50 }, curve = 0.15 });

        var updated = await OrganizerApi.BodyAsync(response);
        updated.GetProperty("seatsPerTier").GetProperty(TierId(evt, 0).ToString()).GetInt32().ShouldBe(600);
        updated.GetProperty("layout").GetProperty("sections")[0].GetProperty("kind").GetString().ShouldBe("seated");

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var indexes = await db.EventSeats.Where(s => s.EventId == Id(evt)).OrderBy(s => s.Index).Select(s => s.Index).ToListAsync(Ct);
        indexes.ShouldBe(Enumerable.Range(0, 600));
    }

    [Fact]
    public async Task Layout_with_wrong_tier_kind_returns_layout_invalid_with_errors()
    {
        var evt = await DraftWithTiersAsync();

        var response = await PutLayoutAsync(evt,
            new { code = "A", tierId = TierId(evt, 1), rows = 0, seatsPerRow = 10, origin = new { x = 0, y = 0 } },
            new { code = "GA", tierId = TierId(evt, 1), kind = "standing" });

        var body = await OrganizerApi.BodyAsync(response, 400);
        body.GetProperty("code").GetString().ShouldBe("layout.invalid");
        var errors = body.GetProperty("errors");
        errors.TryGetProperty("A.tierId", out _).ShouldBeTrue();
        errors.TryGetProperty("A.rows", out _).ShouldBeTrue();
        errors.TryGetProperty("GA.tierId", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Layout_over_the_section_cap_or_past_the_canvas_returns_layout_invalid()
    {
        var evt = await DraftWithTiersAsync();
        var standing = Enumerable.Range(1, 51).Select(i => (object)new { code = $"S{i}", tierId = TierId(evt, 1), kind = "standing" }).ToArray();

        var tooMany = await OrganizerApi.BodyAsync(await PutLayoutAsync(evt, standing), 400);
        var tooWide = await OrganizerApi.BodyAsync(await PutLayoutAsync(evt, new { code = "A", tierId = TierId(evt, 0), rows = 1, seatsPerRow = 100, origin = new { x = 0, y = 0 } }), 400);

        tooMany.GetProperty("code").GetString().ShouldBe("layout.invalid");
        tooMany.GetProperty("errors").TryGetProperty("sections", out _).ShouldBeTrue();
        tooWide.GetProperty("errors").TryGetProperty("A.seatsPerRow", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Relayout_replaces_previous_seats()
    {
        var evt = await DraftWithTiersAsync();
        var first = await OrganizerApi.BodyAsync(await PutLayoutAsync(evt, new { code = "A", tierId = TierId(evt, 0), rows = 10, seatsPerRow = 10, origin = new { x = 0, y = 0 } }));

        var second = await OrganizerApi.BodyAsync(await PutLayoutAsync(first,
            new { code = "B", tierId = TierId(evt, 0), rows = 2, seatsPerRow = 5, origin = new { x = 0, y = 0 } }));

        second.GetProperty("seatsPerTier").GetProperty(TierId(evt, 0).ToString()).GetInt32().ShouldBe(10);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.EventSeats.Where(s => s.EventId == Id(evt)).Select(s => s.Section).Distinct().ToListAsync(Ct)).ShouldBe(["B"]);
    }

    [Fact]
    public async Task Relayout_with_the_same_section_codes_succeeds()
    {
        var evt = await DraftWithTiersAsync();
        var section = new { code = "A", tierId = TierId(evt, 0), rows = 3, seatsPerRow = 4, origin = new { x = 0, y = 0 } };
        var first = await OrganizerApi.BodyAsync(await PutLayoutAsync(evt, section));

        var second = await OrganizerApi.BodyAsync(await PutLayoutAsync(first, section with { }));

        second.GetProperty("seatsPerTier").GetProperty(TierId(evt, 0).ToString()).GetInt32().ShouldBe(12);
    }

    [Fact]
    public async Task Removing_tier_used_by_layout_returns_tier_in_use()
    {
        var evt = await DraftWithTiersAsync();
        var withLayout = await OrganizerApi.BodyAsync(await PutLayoutAsync(evt, new { code = "A", tierId = TierId(evt, 0), rows = 1, seatsPerRow = 4, origin = new { x = 0, y = 0 } }));

        var response = await _api.PutAsync($"/events/{Id(evt)}/tiers", new
        {
            version = Version(withLayout),
            tiers = new object[] { new { id = TierId(evt, 1), name = "Khu đứng", price = 500_000, kind = "GeneralAdmission", capacity = 300, color = Teal } },
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await OrganizerApi.CodeAsync(response)).ShouldBe("tier.in_use");
        (await OrganizerApi.BodyAsync(await _api.GetAsync($"/events/{Id(evt)}"))).GetProperty("tiers").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task Changing_the_kind_of_a_tier_used_by_layout_returns_tier_in_use()
    {
        var evt = await DraftWithTiersAsync();
        var withLayout = await OrganizerApi.BodyAsync(await PutLayoutAsync(evt, new { code = "A", tierId = TierId(evt, 0), rows = 1, seatsPerRow = 4, origin = new { x = 0, y = 0 } }));

        var response = await _api.PutAsync($"/events/{Id(evt)}/tiers", new
        {
            version = Version(withLayout),
            tiers = new object[]
            {
                new { id = TierId(evt, 0), name = "Ghế ngồi", price = 800_000, kind = "GeneralAdmission", capacity = 500, color = Violet },
                new { id = TierId(evt, 1), name = "Khu đứng", price = 500_000, kind = "GeneralAdmission", capacity = 300, color = Teal },
            },
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await OrganizerApi.CodeAsync(response)).ShouldBe("tier.in_use");
    }

    [Fact]
    public async Task Overlapping_phases_return_phase_overlap()
    {
        var draft = await _api.CreateDraftAsync();

        var response = await PutPhasesAsync(draft, Phase(1, 3), Phase(2, 4));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await OrganizerApi.CodeAsync(response)).ShouldBe("event.phase_overlap");
    }

    [Fact]
    public async Task ReplacePhases_keeps_hash_for_null_code_and_clears_for_empty()
    {
        var draft = await _api.CreateDraftAsync();
        var first = await OrganizerApi.BodyAsync(await PutPhasesAsync(draft, Phase(1, 2, "fan-club"), Phase(3, 4, "vip-2026")));
        var keep = first.GetProperty("phases")[0].GetProperty("id").GetGuid();
        var clear = first.GetProperty("phases")[1].GetProperty("id").GetGuid();
        first.GetProperty("phases")[0].GetProperty("hasAccessCode").GetBoolean().ShouldBeTrue();

        var second = await OrganizerApi.BodyAsync(await PutPhasesAsync(first, Phase(1, 2, null, keep), Phase(3, 4, "", clear), Phase(5, 6)));

        second.GetProperty("phases").EnumerateArray().Select(p => p.GetProperty("hasAccessCode").GetBoolean()).ShouldBe([true, false, false]);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IAccessCodeHasher>();
        (await db.Set<SalePhase>().SingleAsync(p => p.Id == keep, Ct)).AccessCodeHash.ShouldBe(hasher.Hash("FAN-CLUB"));
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("có dấu")]
    [InlineData("x;drop")]
    public async Task Access_code_format_is_validated(string code)
    {
        var draft = await _api.CreateDraftAsync();

        (await PutPhasesAsync(draft, Phase(1, 2, code))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Access_code_is_never_returned()
    {
        var draft = await _api.CreateDraftAsync();

        var response = await PutPhasesAsync(draft, Phase(1, 2, "secret-code"));
        var text = await response.Content.ReadAsStringAsync(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        text.ShouldNotContain("secret", Case.Insensitive);
        text.ShouldNotContain("accessCodeHash", Case.Insensitive);
    }

    [Fact]
    public async Task Stale_version_returns_concurrent_edit()
    {
        var evt = await DraftWithTiersAsync();
        (await PutLayoutAsync(evt, new { code = "A", tierId = TierId(evt, 0), rows = 1, seatsPerRow = 1, origin = new { x = 0, y = 0 } }))
            .EnsureSuccessStatusCode();

        var stale = await PutPhasesAsync(evt, Phase(1, 2));

        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await OrganizerApi.CodeAsync(stale)).ShouldBe("event.concurrent_edit");
    }

    // Tier and phase saves only write child rows; they must still check and bump the event's version (xmin).
    [Fact]
    public async Task Stale_tiers_after_a_tiers_save_return_concurrent_edit()
    {
        var evt = await DraftWithTiersAsync();
        (await _api.PutAsync($"/events/{Id(evt)}/tiers", new
        {
            version = Version(evt),
            tiers = new object[] { new { id = TierId(evt, 0), name = "Ghế ngồi", price = 800_000, kind = "Seated", color = Violet }, new { name = "Tab A", price = 1, kind = "Seated", color = Violet } },
        })).EnsureSuccessStatusCode();

        var stale = await _api.PutAsync($"/events/{Id(evt)}/tiers", new
        {
            version = Version(evt),
            tiers = new object[] { new { id = TierId(evt, 0), name = "Ghế ngồi", price = 900_000, kind = "Seated", color = Violet } },
        });

        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await OrganizerApi.CodeAsync(stale)).ShouldBe("event.concurrent_edit");
        (await OrganizerApi.BodyAsync(await _api.GetAsync($"/events/{Id(evt)}"))).GetProperty("tiers").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task Stale_phases_after_a_phases_save_return_concurrent_edit()
    {
        var draft = await _api.CreateDraftAsync();
        (await PutPhasesAsync(draft, Phase(1, 2))).EnsureSuccessStatusCode();

        var stale = await PutPhasesAsync(draft, Phase(3, 4));

        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await OrganizerApi.CodeAsync(stale)).ShouldBe("event.concurrent_edit");
    }

    [Fact]
    public async Task Tiers_and_phases_saves_return_a_new_version()
    {
        var draft = await _api.CreateDraftAsync();

        var withTiers = await DraftWithTiersAsync();
        var afterTiers = await OrganizerApi.BodyAsync(await _api.PutAsync($"/events/{Id(withTiers)}/tiers", new
        {
            version = Version(withTiers),
            tiers = new object[] { new { id = TierId(withTiers, 0), name = "Ghế", price = 1, kind = "Seated", color = Violet } },
        }));
        var afterPhases = await OrganizerApi.BodyAsync(await PutPhasesAsync(draft, Phase(1, 2)));

        Version(afterTiers).ShouldNotBe(Version(withTiers));
        Version(afterPhases).ShouldNotBe(Version(draft));
    }

    [Fact]
    public async Task Editing_tiers_after_publish_returns_not_editable()
    {
        var evt = await DraftWithTiersAsync();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.Events.SingleAsync(e => e.Id == Id(evt), Ct)).Publish();
            await db.SaveChangesAsync(Ct);
        }

        var current = await OrganizerApi.BodyAsync(await _api.GetAsync($"/events/{Id(evt)}"));
        var response = await _api.PutAsync($"/events/{Id(evt)}/tiers", new
        {
            version = Version(current),
            tiers = new object[] { new { name = "Mới", price = 100_000, kind = "Seated", color = Violet } },
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await OrganizerApi.CodeAsync(response)).ShouldBe("event.not_editable");
    }
}
