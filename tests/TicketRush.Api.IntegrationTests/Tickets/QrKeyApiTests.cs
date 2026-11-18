using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Application.Abstractions.Tickets;
using TicketRush.Domain.Events;
using TicketRush.Domain.Tickets;

namespace TicketRush.Api.IntegrationTests.Tickets;

/// <summary><c>GET /tickets/{id}/qr-key</c> (spec 10): owner only, valid tickets of running events only, 10 per minute per user.</summary>
public sealed class QrKeyApiTests(ContainersFixture containers) : IAsyncLifetime
{
    private TicketTestWorld _world = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _world = await TicketTestWorld.CreateAsync(containers);

    public async ValueTask DisposeAsync() => await _world.DisposeAsync();

    private static string QrKeyPath(Guid ticketId) => $"/api/v1/tickets/{ticketId}/qr-key";

    private static async Task<string> CodeOfAsync(HttpResponseMessage response) =>
        (await TicketTestWorld.ReadAsync(response, response.StatusCode)).GetProperty("code").GetString()!;

    [Fact]
    public async Task Qr_key_returns_secret_for_a_valid_ticket_and_no_store()
    {
        var me = await _world.NewUserAsync();
        var ticket = (await _world.IssueSeatedAsync(me, TicketTestWorld.SeatedSlug, 1))[0];

        var response = await _world.GetAsync(QrKeyPath(ticket.Id), me);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        var key = await TicketTestWorld.ReadAsync(response);
        key.EnumerateObject().Select(p => p.Name).ShouldBe(["ticketId", "secret", "stepSeconds", "format"], ignoreOrder: true);
        key.GetProperty("ticketId").GetGuid().ShouldBe(ticket.Id);
        key.GetProperty("stepSeconds").GetInt32().ShouldBe(15);
        key.GetProperty("format").GetString().ShouldBe("TR1");
        var secret = Convert.FromBase64String(key.GetProperty("secret").GetString()!);
        secret.Length.ShouldBe(32);
        secret.ShouldBe(await _world.SecretOfAsync(ticket.Id));
    }

    [Fact]
    public async Task A_code_built_from_the_returned_key_verifies_with_QrCodeService()
    {
        var me = await _world.NewUserAsync();
        var ticket = (await _world.IssueGaAsync(me, TicketTestWorld.GaSlug))[0];
        var key = await _world.JsonAsync(QrKeyPath(ticket.Id), me);
        var qr = _world.Factory.Services.GetRequiredService<IQrCodeService>();
        var secret = Convert.FromBase64String(key.GetProperty("secret").GetString()!);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var code = qr.Encode(key.GetProperty("ticketId").GetGuid(), secret, now);

        qr.TryParse(code, out var parsed).ShouldBeTrue();
        parsed!.TicketId.ShouldBe(ticket.Id);
        qr.Verify(parsed, await _world.SecretOfAsync(ticket.Id), now).ShouldBeTrue();
        qr.Verify(parsed, await _world.SecretOfAsync(ticket.Id), now + 15).ShouldBeTrue("one step of drift is accepted");
        qr.Verify(parsed, await _world.SecretOfAsync(ticket.Id), now + 45).ShouldBeFalse();
    }

    [Theory]
    [InlineData(TicketStatus.CheckedIn)]
    [InlineData(TicketStatus.Listed)]
    [InlineData(TicketStatus.Voided)]
    public async Task Qr_key_is_refused_for_checked_in_listed_and_voided_tickets(TicketStatus status)
    {
        var me = await _world.NewUserAsync();
        var ticket = (await _world.IssueGaAsync(me, TicketTestWorld.GaSlug))[0];
        await _world.SetTicketStatusAsync(ticket.Id, status);

        var response = await _world.GetAsync(QrKeyPath(ticket.Id), me);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        (await TicketTestWorld.ReadAsync(response, HttpStatusCode.Conflict)).GetProperty("code").GetString().ShouldBe("ticket.not_displayable");
    }

    [Theory]
    [InlineData("ended-by-time")]
    [InlineData("ended-status")]
    [InlineData("cancelled")]
    [InlineData("draft")]
    public async Task Qr_key_is_refused_after_the_event_ended_or_was_cancelled(string situation)
    {
        var me = await _world.NewUserAsync();
        var ticket = (await _world.IssueGaAsync(me, TicketTestWorld.GaSlug))[0];
        var now = DateTimeOffset.UtcNow;
        await (situation switch
        {
            "ended-by-time" => _world.SetEventAsync(ticket.EventId, startsAt: now.AddHours(-5), endsAt: now.AddSeconds(-1)),
            "ended-status" => _world.SetEventAsync(ticket.EventId, status: EventStatus.Ended),
            "cancelled" => _world.SetEventAsync(ticket.EventId, status: EventStatus.Cancelled),
            _ => _world.SetEventAsync(ticket.EventId, status: EventStatus.Draft),
        });

        var response = await _world.GetAsync(QrKeyPath(ticket.Id), me);

        (await TicketTestWorld.ReadAsync(response, HttpStatusCode.Conflict)).GetProperty("code").GetString().ShouldBe("ticket.not_displayable");
    }

    [Fact]
    public async Task Qr_key_still_works_while_the_event_is_running()
    {
        var me = await _world.NewUserAsync();
        var ticket = (await _world.IssueGaAsync(me, TicketTestWorld.GaSlug))[0];
        var now = DateTimeOffset.UtcNow;
        await _world.SetEventAsync(ticket.EventId, startsAt: now.AddHours(-1), endsAt: now.AddHours(2));

        (await _world.GetAsync(QrKeyPath(ticket.Id), me)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Qr_key_is_limited_to_10_per_minute_per_user()
    {
        var me = await _world.NewUserAsync();
        var other = await _world.NewUserAsync();
        var mine = (await _world.IssueGaAsync(me, TicketTestWorld.GaSlug))[0];
        var theirs = (await _world.IssueGaAsync(other, TicketTestWorld.GaSlug))[0];

        for (var i = 0; i < 10; i++)
        {
            (await _world.GetAsync(QrKeyPath(mine.Id), me)).StatusCode.ShouldBe(HttpStatusCode.OK, $"request {i + 1}");
        }

        _world.Factory.Queries.Reset();
        var limited = await _world.GetAsync(QrKeyPath(mine.Id), me);

        limited.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter.ShouldNotBeNull();
        limited.Headers.RetryAfter.Delta!.Value.TotalSeconds.ShouldBeInRange(1, 60);
        limited.Headers.CacheControl!.NoStore.ShouldBeTrue();
        var problem = await TicketTestWorld.ReadAsync(limited, HttpStatusCode.TooManyRequests);
        problem.GetProperty("code").GetString().ShouldBe("ticket.qr_key_rate_limited");
        problem.GetProperty("retryAfterSeconds").GetInt32().ShouldBe((int)limited.Headers.RetryAfter.Delta!.Value.TotalSeconds);
        _world.Factory.Queries.Matching(sql => sql.Contains("tickets", StringComparison.Ordinal)).ShouldBeEmpty("the throttle runs before the database");
        (await _world.GetAsync(QrKeyPath(theirs.Id), other)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _world.GetAsync($"/api/v1/tickets/{mine.Id}", me)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Qr_key_for_a_ticket_owned_by_someone_else_consumes_the_callers_slot_but_is_404()
    {
        var owner = await _world.NewUserAsync();
        var prober = await _world.NewUserAsync();
        var ticket = (await _world.IssueGaAsync(owner, TicketTestWorld.GaSlug))[0];

        for (var i = 0; i < 10; i++)
        {
            var path = i % 2 == 0 ? QrKeyPath(ticket.Id) : QrKeyPath(Guid.NewGuid());
            (await CodeOfAsync(await _world.GetAsync(path, prober))).ShouldBe("ticket.not_found");
        }

        (await _world.GetAsync(QrKeyPath(ticket.Id), prober)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await _world.GetAsync(QrKeyPath(ticket.Id), owner)).StatusCode.ShouldBe(HttpStatusCode.OK, "the owner has a slot of their own");
    }

    [Fact]
    public async Task A_409_also_consumes_a_slot()
    {
        var me = await _world.NewUserAsync();
        var ticket = (await _world.IssueGaAsync(me, TicketTestWorld.GaSlug))[0];
        await _world.SetTicketStatusAsync(ticket.Id, TicketStatus.Listed);

        for (var i = 0; i < 10; i++)
        {
            (await _world.GetAsync(QrKeyPath(ticket.Id), me)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

        (await _world.GetAsync(QrKeyPath(ticket.Id), me)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Throttle_fails_closed_when_redis_is_down()
    {
        var me = await _world.NewUserAsync();
        var ticket = (await _world.IssueGaAsync(me, TicketTestWorld.GaSlug))[0];
        await using var broken = _world.Factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IQrKeyThrottle, DownThrottle>()));
        using var client = broken.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, QrKeyPath(ticket.Id));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", me.Access);
        _world.Factory.Queries.Reset();

        var response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        (await response.Content.ReadAsStringAsync(Ct)).ShouldNotContain("secret", Case.Insensitive);
        (await TicketTestWorld.ReadAsync(response, HttpStatusCode.ServiceUnavailable)).GetProperty("code").GetString().ShouldBe("system.busy");
        _world.Factory.Queries.Matching(sql => sql.Contains("tickets", StringComparison.Ordinal)).ShouldBeEmpty();
    }

    private sealed class DownThrottle : IQrKeyThrottle
    {
        public Task<ThrottleResult> TryAcquireAsync(Guid userId, CancellationToken ct) =>
            Task.FromException<ThrottleResult>(new ThrottleUnavailableException("down"));
    }
}
