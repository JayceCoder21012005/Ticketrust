using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TicketRush.Api.IntegrationTests.Auth;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Application.Abstractions.Tickets;
using TicketRush.Domain.Gates;
using TicketRush.Domain.Tickets;
using TicketRush.Infrastructure.Identity;
using TicketRush.Infrastructure.Persistence.Seed;

namespace TicketRush.Api.IntegrationTests.Tickets;

/// <summary>Gate check-in (spec 11): assignment, the scan rules, the atomic admission, the log and the counters.</summary>
public sealed class GateApiTests(ContainersFixture containers) : IAsyncLifetime
{
    private const string OrganizerEmail = "organizer@ticketrush.dev";
    private const string GateEmail = "gate@ticketrush.dev";

    private TicketTestWorld _world = null!;
    private TicketTestWorld.User _organizer = null!;
    private TicketTestWorld.User _gate = null!;
    private Guid _eventId;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IQrCodeService Qr => _world.Factory.Services.GetRequiredService<IQrCodeService>();

    public async ValueTask InitializeAsync()
    {
        _world = await TicketTestWorld.CreateAsync(containers);
        _organizer = await LoginAsync(OrganizerEmail);
        _gate = await LoginAsync(GateEmail);
        _eventId = await _world.EventIdAsync(TicketTestWorld.GaSlug);
        (await AssignAsync(_eventId, GateEmail, "Cổng A")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    public async ValueTask DisposeAsync() => await _world.DisposeAsync();

    private async Task<TicketTestWorld.User> LoginAsync(string email)
    {
        var login = await LoginResult.ReadAsync(await _world.Auth.LoginAsync(email, DevDataSeeder.DevPassword));
        return new TicketTestWorld.User(Guid.Empty, login.AccessToken, email);
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, TicketTestWorld.User? who, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (who is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", who.Access);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return _world.Client.SendAsync(request, Ct);
    }

    private Task<HttpResponseMessage> AssignAsync(Guid eventId, string email, string gateName) =>
        SendAsync(HttpMethod.Post, $"/api/v1/organizer/events/{eventId}/gate-staff", _organizer, new { email, gateName });

    private Task<HttpResponseMessage> ScanAsync(Guid eventId, string payload, TicketTestWorld.User? who = null) =>
        SendAsync(HttpMethod.Post, $"/api/v1/gate/events/{eventId}/scan", who ?? _gate, new { payload });

    private async Task<string> CodeAsync(Guid ticketId, int stepOffset = 0)
    {
        var secret = await _world.SecretOfAsync(ticketId);
        return Qr.Encode(ticketId, secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + (stepOffset * IQrCodeService.StepSeconds));
    }

    private async Task<JsonElement> ScanOkAsync(Guid eventId, string payload, TicketTestWorld.User? who = null) =>
        await TicketTestWorld.ReadAsync(await ScanAsync(eventId, payload, who));

    private async Task<IssuedTicket> NewGaTicketAsync(string slug = TicketTestWorld.GaSlug) =>
        (await _world.IssueGaAsync(await _world.NewUserAsync("Lê Thị Hương"), slug))[0];

    private Task<List<CheckIn>> LogOfAsync(Guid eventId) =>
        _world.WithDbAsync(db => db.CheckIns.AsNoTracking().Where(c => c.EventId == eventId).OrderBy(c => c.ScannedAt).ToListAsync(Ct));

    [Fact]
    public async Task A_valid_ticket_is_admitted_with_its_seat_and_holder_and_is_checked_in()
    {
        var holder = await _world.NewUserAsync("Lê Thị Hương");
        var ticket = (await _world.IssueSeatedAsync(holder, TicketTestWorld.SeatedSlug, 1))[0];
        await AssignAsync(ticket.EventId, GateEmail, "Cổng B");

        var body = await ScanOkAsync(ticket.EventId, await CodeAsync(ticket.Id));

        body.GetProperty("result").GetString().ShouldBe("Admitted");
        body.GetProperty("ticket").GetProperty("holderName").GetString().ShouldBe("Lê Thị Hương");
        body.GetProperty("ticket").GetProperty("section").GetString().ShouldNotBeNullOrEmpty();
        body.GetProperty("ticket").GetProperty("tier").GetString().ShouldNotBeNullOrEmpty();
        body.GetProperty("checkedInAt").GetDateTimeOffset().ShouldBeInRange(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
        var stored = await _world.WithDbAsync(db => db.Tickets.AsNoTracking().SingleAsync(t => t.Id == ticket.Id, Ct));
        stored.Status.ShouldBe(TicketStatus.CheckedIn);
        stored.CheckedInAt.ShouldNotBeNull();
        (await LogOfAsync(ticket.EventId)).ShouldContain(c => c.TicketId == ticket.Id && c.Result == ScanResult.Admitted && c.GateName == "Cổng B");
    }

    [Fact]
    public async Task The_same_code_read_twice_in_a_row_gets_the_first_answer_and_one_log_line()
    {
        var ticket = await NewGaTicketAsync();
        var code = await CodeAsync(ticket.Id);

        var first = await ScanOkAsync(_eventId, code);
        var second = await ScanOkAsync(_eventId, code);

        first.GetProperty("result").GetString().ShouldBe("Admitted");
        second.GetProperty("result").GetString().ShouldBe("Admitted");
        (await LogOfAsync(_eventId)).Count(c => c.TicketId == ticket.Id).ShouldBe(1);
    }

    [Fact]
    public async Task A_second_scan_of_an_admitted_ticket_says_when_and_where_it_was_first_admitted()
    {
        var ticket = await NewGaTicketAsync();
        await ScanOkAsync(_eventId, await CodeAsync(ticket.Id));

        var again = await ScanOkAsync(_eventId, await CodeAsync(ticket.Id, stepOffset: 1)); // another code: not the camera's double read

        again.GetProperty("result").GetString().ShouldBe("AlreadyCheckedIn");
        again.GetProperty("gateName").GetString().ShouldBe("Cổng A");
        again.GetProperty("checkedInAt").GetDateTimeOffset().ShouldBeInRange(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
        again.GetProperty("ticket").GetProperty("holderName").GetString().ShouldBe("Lê Thị Hương");
    }

    [Fact]
    public async Task Two_staff_scanning_the_same_ticket_at_once_admit_it_exactly_once()
    {
        var second = await SecondGateStaffAsync();
        await AssignAsync(_eventId, second.Email, "Cổng C");
        for (var round = 0; round < 5; round++)
        {
            var ticket = await NewGaTicketAsync();
            var code = await CodeAsync(ticket.Id);

            var results = await Task.WhenAll(ScanOkAsync(_eventId, code, _gate), ScanOkAsync(_eventId, code, second.User));

            results.Select(r => r.GetProperty("result").GetString()).Order().ShouldBe(["Admitted", "AlreadyCheckedIn"]);
            (await LogOfAsync(_eventId)).Count(c => c.TicketId == ticket.Id).ShouldBe(2);
        }
    }

    [Fact]
    public async Task A_screenshot_older_than_30_seconds_is_expired()
    {
        var ticket = await NewGaTicketAsync();

        var body = await ScanOkAsync(_eventId, await CodeAsync(ticket.Id, stepOffset: -3));

        body.GetProperty("result").GetString().ShouldBe("Expired");
        (await _world.WithDbAsync(db => db.Tickets.AsNoTracking().SingleAsync(t => t.Id == ticket.Id, Ct))).Status.ShouldBe(TicketStatus.Valid);
        (await LogOfAsync(_eventId)).ShouldContain(c => c.TicketId == ticket.Id && c.Result == ScanResult.Expired);
    }

    [Fact]
    public async Task A_ticket_of_another_event_is_wrong_event_and_stays_valid()
    {
        var other = await NewGaTicketAsync(TicketTestWorld.OtherGaSlug);

        var body = await ScanOkAsync(_eventId, await CodeAsync(other.Id));

        body.GetProperty("result").GetString().ShouldBe("WrongEvent");
        (await _world.WithDbAsync(db => db.Tickets.AsNoTracking().SingleAsync(t => t.Id == other.Id, Ct))).Status.ShouldBe(TicketStatus.Valid);
        (await LogOfAsync(_eventId)).ShouldContain(c => c.TicketId == other.Id && c.Result == ScanResult.WrongEvent);
    }

    [Theory]
    [InlineData(TicketStatus.Voided, "voided")]
    [InlineData(TicketStatus.Listed, "listed")]
    public async Task A_voided_or_listed_ticket_is_not_valid(TicketStatus status, string reason)
    {
        var ticket = await NewGaTicketAsync();
        await _world.SetTicketStatusAsync(ticket.Id, status);

        var body = await ScanOkAsync(_eventId, await CodeAsync(ticket.Id));

        body.GetProperty("result").GetString().ShouldBe("NotValid");
        body.GetProperty("reason").GetString().ShouldBe(reason);
    }

    [Fact]
    public async Task Garbage_and_forged_codes_are_invalid_and_still_logged()
    {
        var ticket = await NewGaTicketAsync();
        var forged = (await CodeAsync(ticket.Id))[..^3] + "AAA";

        var garbage = await ScanOkAsync(_eventId, "not a ticket");
        var bad = await ScanOkAsync(_eventId, forged);

        garbage.GetProperty("result").GetString().ShouldBe("Invalid");
        garbage.GetProperty("reason").GetString().ShouldBe("bad_format");
        bad.GetProperty("result").GetString().ShouldBe("Invalid");
        bad.GetProperty("reason").GetString().ShouldBe("bad_signature");
        (await LogOfAsync(_eventId)).Count(c => c.Result == ScanResult.Invalid).ShouldBe(2);
        (await _world.WithDbAsync(db => db.Tickets.AsNoTracking().SingleAsync(t => t.Id == ticket.Id, Ct))).Status.ShouldBe(TicketStatus.Valid);
    }

    [Fact]
    public async Task Staff_who_are_not_assigned_get_404_and_nothing_is_logged()
    {
        var ticket = await NewGaTicketAsync();
        var other = await _world.EventIdAsync(TicketTestWorld.ThirdGaSlug);

        var response = await ScanAsync(other, await CodeAsync(ticket.Id));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await LogOfAsync(other)).ShouldBeEmpty();
        (await SendAsync(HttpMethod.Get, $"/api/v1/gate/events/{other}/stats", _gate)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Only_gate_staff_may_scan()
    {
        var customer = await _world.NewUserAsync();

        (await ScanAsync(_eventId, "x", customer)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Post, $"/api/v1/gate/events/{_eventId}/scan", null, new { payload = "x" })).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ScanAsync(_eventId, "x", _organizer)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_blank_payload_is_a_validation_error()
    {
        (await ScanAsync(_eventId, "  ")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task More_than_five_scans_in_a_second_are_refused_with_retry_after()
    {
        var statuses = new List<HttpResponseMessage>();
        for (var i = 0; i < 12; i++)
        {
            statuses.Add(await ScanAsync(_eventId, $"burst-{i}"));
        }

        var refused = statuses.FirstOrDefault(r => r.StatusCode == HttpStatusCode.TooManyRequests);
        refused.ShouldNotBeNull();
        refused.Headers.RetryAfter.ShouldNotBeNull();
        (await TicketTestWorld.ReadAsync(refused, HttpStatusCode.TooManyRequests)).GetProperty("code").GetString().ShouldBe("rate_limited");
    }

    [Fact]
    public async Task Stats_count_the_checked_in_tickets_and_the_last_minute()
    {
        var before = await _world.JsonAsync($"/api/v1/gate/events/{_eventId}/stats", _gate);
        var ticket = await NewGaTicketAsync();
        await ScanOkAsync(_eventId, await CodeAsync(ticket.Id));

        var after = await _world.JsonAsync($"/api/v1/gate/events/{_eventId}/stats", _gate);

        after.GetProperty("checkedIn").GetInt32().ShouldBe(before.GetProperty("checkedIn").GetInt32() + 1);
        after.GetProperty("total").GetInt32().ShouldBe(before.GetProperty("total").GetInt32() + 1);
        after.GetProperty("lastMinute").GetInt32().ShouldBe(before.GetProperty("lastMinute").GetInt32() + 1);
    }

    [Fact]
    public async Task The_gate_lists_only_its_assigned_events()
    {
        var events = await _world.JsonAsync("/api/v1/gate/events", _gate);

        events.EnumerateArray().Select(e => e.GetProperty("eventId").GetGuid()).ShouldBe([_eventId]);
        events[0].GetProperty("gateName").GetString().ShouldBe("Cổng A");
    }

    [Fact]
    public async Task Assigning_again_renames_the_gate_and_removing_ends_access_without_deleting_the_log()
    {
        var ticket = await NewGaTicketAsync();
        await ScanOkAsync(_eventId, await CodeAsync(ticket.Id));

        (await AssignAsync(_eventId, GateEmail, "Cổng Đông")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await _world.JsonAsync("/api/v1/gate/events", _gate))[0].GetProperty("gateName").GetString().ShouldBe("Cổng Đông");

        var removed = await SendAsync(HttpMethod.Delete, $"/api/v1/organizer/events/{_eventId}/gate-staff", _organizer, new { email = GateEmail });
        removed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await ScanAsync(_eventId, "x")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await LogOfAsync(_eventId)).ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Organizers_only_manage_their_own_events_and_only_gate_staff_accounts()
    {
        (await AssignAsync(Guid.CreateVersion7(), GateEmail, "A")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var customer = await _world.NewUserAsync();
        (await AssignAsync(_eventId, customer.FullName, "A")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AssignAsync(_eventId, "nobody@example.com", "A")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await AssignAsync(_eventId, GateEmail, " ")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await SendAsync(HttpMethod.Post, $"/api/v1/organizer/events/{_eventId}/gate-staff", _gate, new { email = GateEmail, gateName = "A" }))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private async Task<(string Email, TicketTestWorld.User User)> SecondGateStaffAsync()
    {
        var email = $"gate-{Guid.NewGuid():N}@example.com";
        await using (var scope = _world.Factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = new AppUser { Id = Guid.CreateVersion7(), Email = email, UserName = email, FullName = "Nhân viên cổng 2", EmailConfirmed = true };
            (await users.CreateAsync(user, AuthApi.Password)).Succeeded.ShouldBeTrue();
            (await users.AddToRoleAsync(user, "GateStaff")).Succeeded.ShouldBeTrue();
        }

        var login = await LoginResult.ReadAsync(await _world.Auth.LoginAsync(email));
        return (email, new TicketTestWorld.User(Guid.Empty, login.AccessToken, email));
    }
}
