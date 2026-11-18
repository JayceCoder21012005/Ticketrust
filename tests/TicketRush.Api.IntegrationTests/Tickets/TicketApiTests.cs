using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Domain.Events;
using TicketRush.Domain.Tickets;

namespace TicketRush.Api.IntegrationTests.Tickets;

/// <summary>Spec 10 wallet endpoints over HTTP: <c>GET /me/tickets</c> and <c>GET /tickets/{id}</c>, owner only.</summary>
public sealed class TicketApiTests(ContainersFixture containers) : IAsyncLifetime
{
    private TicketTestWorld _world = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _world = await TicketTestWorld.CreateAsync(containers);

    public async ValueTask DisposeAsync() => await _world.DisposeAsync();

    private static List<Guid> IdsOf(JsonElement tickets) => [.. tickets.EnumerateArray().Select(t => t.GetProperty("id").GetGuid())];

    /// <summary>Postgres orders uuids bytewise, which is the order of their canonical hex text (not <see cref="Guid.CompareTo(Guid)"/>).</summary>
    private static List<Guid> DbOrder(IEnumerable<Guid> ids) => [.. ids.OrderBy(id => id.ToString("D"), StringComparer.Ordinal)];

    private static List<string> SlugsOf(JsonElement list) => [.. list.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("slug").GetString()!)];

    private static JsonElement GroupOf(JsonElement list, string slug) =>
        list.GetProperty("events").EnumerateArray().Single(e => e.GetProperty("slug").GetString() == slug);

    [Fact]
    public async Task List_groups_tickets_by_event_with_seat_labels()
    {
        var me = await _world.NewUserAsync();
        var seated = await _world.IssueSeatedAsync(me, TicketTestWorld.SeatedSlug, 2);
        var ga = await _world.IssueGaAsync(me, TicketTestWorld.GaSlug, 2);

        var list = await _world.JsonAsync("/api/v1/me/tickets", me);

        list.EnumerateObject().Select(p => p.Name).ShouldBe(["events"]);
        SlugsOf(list).ShouldBe([TicketTestWorld.SeatedSlug, TicketTestWorld.GaSlug], "upcoming events sort by start, earliest first");
        var seatedGroup = GroupOf(list, TicketTestWorld.SeatedSlug);
        seatedGroup.EnumerateObject().Select(p => p.Name).ShouldBe(
            ["eventId", "slug", "title", "coverImageUrl", "startsAt", "endsAt", "venueName", "city", "status", "tickets"], ignoreOrder: true);
        seatedGroup.GetProperty("eventId").GetGuid().ShouldBe(seated[0].EventId);
        seatedGroup.GetProperty("title").GetString().ShouldBe("Sơn Tùng M-TP Live");
        seatedGroup.GetProperty("venueName").GetString().ShouldBe("Sân vận động Mỹ Đình");
        seatedGroup.GetProperty("city").GetString().ShouldBe("Hà Nội");
        seatedGroup.GetProperty("status").GetString().ShouldBe("Published");
        var tickets = seatedGroup.GetProperty("tickets");
        IdsOf(tickets).ShouldBe(DbOrder(seated.Select(t => t.Id)));

        var labels = await _world.WithDbAsync(async db => await db.EventSeats.AsNoTracking()
            .Where(s => s.EventId == seated[0].EventId && seated.Select(t => t.SeatIndex!.Value).Contains(s.Index))
            .ToDictionaryAsync(s => s.Index, s => (s.Section, s.Row, s.Number), Ct));
        foreach (var ticket in tickets.EnumerateArray())
        {
            ticket.EnumerateObject().Select(p => p.Name).ShouldBe(["id", "orderId", "tierName", "seat", "status", "holderName"], ignoreOrder: true);
            var issued = seated.Single(t => t.Id == ticket.GetProperty("id").GetGuid());
            var expected = labels[issued.SeatIndex!.Value];
            var seat = ticket.GetProperty("seat");
            seat.GetProperty("section").GetString().ShouldBe(expected.Section);
            seat.GetProperty("row").GetString().ShouldBe(expected.Row);
            seat.GetProperty("number").GetString().ShouldBe(expected.Number);
            ticket.GetProperty("orderId").GetGuid().ShouldBe(issued.OrderId);
            ticket.GetProperty("status").GetString().ShouldBe("Valid");
            ticket.GetProperty("holderName").GetString().ShouldBe("Nguyễn Văn Đạt");
            ticket.GetProperty("tierName").GetString().ShouldNotBeNullOrWhiteSpace();
        }

        var gaTickets = GroupOf(list, TicketTestWorld.GaSlug).GetProperty("tickets");
        IdsOf(gaTickets).ShouldBe(DbOrder(ga.Select(t => t.Id)));
        gaTickets.EnumerateArray().ShouldAllBe(t => t.GetProperty("seat").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task A_general_admission_ticket_has_no_seat_on_detail_too()
    {
        var me = await _world.NewUserAsync();
        var ticket = (await _world.IssueGaAsync(me, TicketTestWorld.GaSlug))[0];

        var detail = await _world.JsonAsync($"/api/v1/tickets/{ticket.Id}", me);

        detail.GetProperty("seat").ValueKind.ShouldBe(JsonValueKind.Null);
        detail.GetProperty("tierName").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Upcoming_and_past_scopes_split_by_event_end_and_cancellation()
    {
        var me = await _world.NewUserAsync();
        var now = DateTimeOffset.UtcNow;
        var live = await _world.IssueSeatedAsync(me, TicketTestWorld.SeatedSlug, 1);
        var ended = await _world.IssueGaAsync(me, TicketTestWorld.GaSlug);
        await _world.SetEventAsync(ended[0].EventId, startsAt: now.AddDays(-3), endsAt: now.AddDays(-2));
        var cancelled = await _world.IssueGaAsync(me, TicketTestWorld.OtherGaSlug);
        await _world.SetEventAsync(cancelled[0].EventId, status: EventStatus.Cancelled);
        var mixed = await _world.IssueGaAsync(me, TicketTestWorld.ThirdGaSlug, 2);
        await _world.SetTicketStatusAsync(mixed[1].Id, TicketStatus.Voided);

        var upcoming = await _world.JsonAsync("/api/v1/me/tickets?scope=upcoming", me);
        var past = await _world.JsonAsync("/api/v1/me/tickets?scope=past", me);
        var byDefault = await _world.JsonAsync("/api/v1/me/tickets", me);

        SlugsOf(upcoming).ShouldBe([TicketTestWorld.ThirdGaSlug, TicketTestWorld.SeatedSlug], "ascending by event start");
        IdsOf(GroupOf(upcoming, TicketTestWorld.ThirdGaSlug).GetProperty("tickets")).ShouldBe([mixed[0].Id]);
        IdsOf(GroupOf(upcoming, TicketTestWorld.SeatedSlug).GetProperty("tickets")).ShouldBe([live[0].Id]);
        SlugsOf(past).ShouldBe([TicketTestWorld.OtherGaSlug, TicketTestWorld.ThirdGaSlug, TicketTestWorld.GaSlug], "descending by event start");
        IdsOf(GroupOf(past, TicketTestWorld.ThirdGaSlug).GetProperty("tickets")).ShouldBe([mixed[1].Id]);
        GroupOf(past, TicketTestWorld.OtherGaSlug).GetProperty("status").GetString().ShouldBe("Cancelled");
        byDefault.GetRawText().ShouldBe(upcoming.GetRawText());
    }

    [Fact]
    public async Task An_event_marked_ended_is_past_even_before_its_scheduled_end()
    {
        var me = await _world.NewUserAsync();
        var ticket = (await _world.IssueSeatedAsync(me, TicketTestWorld.SeatedSlug, 1))[0];
        await _world.SetEventAsync(ticket.EventId, status: EventStatus.Ended);

        (await _world.JsonAsync("/api/v1/me/tickets?scope=PAST", me)).GetProperty("events").GetArrayLength().ShouldBe(1);
        (await _world.JsonAsync("/api/v1/me/tickets?scope=upcoming", me)).GetProperty("events").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task An_invalid_scope_is_400()
    {
        var me = await _world.NewUserAsync();

        var problem = await _world.JsonAsync("/api/v1/me/tickets?scope=future", me, HttpStatusCode.BadRequest);

        problem.GetProperty("code").GetString().ShouldBe("ticket.invalid_scope");
        problem.GetProperty("errors").GetProperty("scope").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task A_user_without_tickets_gets_an_empty_list()
    {
        var me = await _world.NewUserAsync();

        (await _world.JsonAsync("/api/v1/me/tickets", me)).GetProperty("events").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Another_users_ticket_is_404_on_detail_and_qr_key_and_list_excludes_it()
    {
        var owner = await _world.NewUserAsync();
        var other = await _world.NewUserAsync("Trần Thị Bích");
        var ticket = (await _world.IssueSeatedAsync(owner, TicketTestWorld.SeatedSlug, 1))[0];

        (await _world.JsonAsync($"/api/v1/tickets/{ticket.Id}", other, HttpStatusCode.NotFound)).GetProperty("code").GetString().ShouldBe("ticket.not_found");
        (await _world.JsonAsync($"/api/v1/tickets/{ticket.Id}/qr-key", other, HttpStatusCode.NotFound)).GetProperty("code").GetString().ShouldBe("ticket.not_found");
        (await _world.JsonAsync("/api/v1/me/tickets", other)).GetProperty("events").GetArrayLength().ShouldBe(0);
        (await _world.JsonAsync("/api/v1/me/tickets?scope=past", other)).GetProperty("events").GetArrayLength().ShouldBe(0);
        (await _world.GetAsync($"/api/v1/tickets/{ticket.Id}", owner)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Unknown_ticket_id_is_404_with_the_same_body()
    {
        var owner = await _world.NewUserAsync();
        var other = await _world.NewUserAsync();
        var ticket = (await _world.IssueGaAsync(owner, TicketTestWorld.GaSlug))[0];
        var unknown = Guid.NewGuid();

        foreach (var suffix in new[] { string.Empty, "/qr-key" })
        {
            var foreign = await _world.GetAsync($"/api/v1/tickets/{ticket.Id}{suffix}", other);
            var missing = await _world.GetAsync($"/api/v1/tickets/{unknown}{suffix}", other);

            foreign.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            Normalized(await foreign.Content.ReadAsStringAsync(Ct)).ShouldBe(Normalized(await missing.Content.ReadAsStringAsync(Ct)));
            foreign.Content.Headers.ContentType!.ToString().ShouldBe(missing.Content.Headers.ContentType!.ToString());
        }
    }

    /// <summary>The body minus what legitimately differs per request: the path (it holds the id) and the correlation ids.</summary>
    private static string Normalized(string body)
    {
        var json = JsonNode.Parse(body)!.AsObject();
        foreach (var name in new[] { "instance", "correlationId", "traceId" })
        {
            json.Remove(name);
        }

        json["code"]!.GetValue<string>().ShouldBe("ticket.not_found");
        return json.ToJsonString();
    }

    [Fact]
    public async Task Detail_lists_sibling_tickets_of_the_order()
    {
        var me = await _world.NewUserAsync();
        var order = await _world.IssueSeatedAsync(me, TicketTestWorld.SeatedSlug, 3);
        var another = await _world.IssueSeatedAsync(me, TicketTestWorld.SeatedSlug, 1);
        var expected = DbOrder(order.Select(t => t.Id));

        var detail = await _world.JsonAsync($"/api/v1/tickets/{expected[1]}", me);

        detail.GetProperty("orderTicketIds").EnumerateArray().Select(i => i.GetGuid()).ToList().ShouldBe(expected);
        detail.GetProperty("id").GetGuid().ShouldBe(expected[1]);
        detail.GetProperty("orderId").GetGuid().ShouldBe(order[0].OrderId);
        detail.GetProperty("holderName").GetString().ShouldBe("Nguyễn Văn Đạt");
        detail.GetProperty("status").GetString().ShouldBe("Valid");
        detail.GetProperty("seat").GetProperty("section").GetString().ShouldNotBeNullOrWhiteSpace();
        var evt = detail.GetProperty("event");
        evt.EnumerateObject().Select(p => p.Name).ShouldBe(
            ["eventId", "slug", "title", "coverImageUrl", "startsAt", "endsAt", "venueName", "city", "status"], ignoreOrder: true);
        evt.GetProperty("slug").GetString().ShouldBe(TicketTestWorld.SeatedSlug);
        evt.GetProperty("venueName").GetString().ShouldBe("Sân vận động Mỹ Đình");
        (await _world.JsonAsync($"/api/v1/tickets/{another[0].Id}", me)).GetProperty("orderTicketIds").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task The_secret_is_never_in_list_or_detail()
    {
        var me = await _world.NewUserAsync();
        var ticket = (await _world.IssueSeatedAsync(me, TicketTestWorld.SeatedSlug, 1))[0];
        var secret = await _world.SecretOfAsync(ticket.Id);
        var encodings = new[]
        {
            Convert.ToBase64String(secret),
            Convert.ToBase64String(secret).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
            Convert.ToHexStringLower(secret),
            Convert.ToHexString(secret),
        };

        var bodies = new[]
        {
            (await _world.JsonAsync("/api/v1/me/tickets", me)).GetRawText(),
            (await _world.JsonAsync("/api/v1/me/tickets?scope=past", me)).GetRawText(),
            (await _world.JsonAsync($"/api/v1/tickets/{ticket.Id}", me)).GetRawText(),
        };
        bodies[0].ShouldContain(ticket.Id.ToString(), Case.Sensitive);

        foreach (var body in bodies)
        {
            body.ShouldNotContain("secret", Case.Insensitive);
            body.ShouldNotContain("qrKey", Case.Insensitive);
            encodings.ShouldAllBe(e => !body.Contains(e, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Anonymous_is_401()
    {
        var me = await _world.NewUserAsync();
        var ticket = (await _world.IssueGaAsync(me, TicketTestWorld.GaSlug))[0];

        foreach (var path in new[] { "/api/v1/me/tickets", $"/api/v1/tickets/{ticket.Id}", $"/api/v1/tickets/{ticket.Id}/qr-key" })
        {
            (await _world.GetAsync(path, null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized, path);
        }
    }

    [Fact]
    public async Task List_and_detail_are_no_store_including_the_404()
    {
        var me = await _world.NewUserAsync();
        var ticket = (await _world.IssueGaAsync(me, TicketTestWorld.GaSlug))[0];

        foreach (var path in new[] { "/api/v1/me/tickets", "/api/v1/me/tickets?scope=bad", $"/api/v1/tickets/{ticket.Id}", $"/api/v1/tickets/{Guid.NewGuid()}" })
        {
            var response = await _world.GetAsync(path, me);
            response.Headers.CacheControl.ShouldNotBeNull(path);
            response.Headers.CacheControl.NoStore.ShouldBeTrue(path);
        }
    }

    [Fact]
    public async Task The_list_is_capped_at_200_tickets()
    {
        var me = await _world.NewUserAsync();
        await _world.IssueGaAsync(me, TicketTestWorld.GaSlug, 100);
        await _world.IssueGaAsync(me, TicketTestWorld.OtherGaSlug, 100);
        await _world.IssueGaAsync(me, TicketTestWorld.ThirdGaSlug, 50);

        var list = await _world.JsonAsync("/api/v1/me/tickets", me);

        list.GetProperty("events").EnumerateArray().Sum(e => e.GetProperty("tickets").GetArrayLength()).ShouldBe(200);
    }
}
