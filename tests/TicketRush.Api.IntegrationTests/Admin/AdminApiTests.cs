using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TicketRush.Api.IntegrationTests.Auth;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Api.IntegrationTests.Tickets;
using TicketRush.Application.Abstractions.Inventory;
using TicketRush.Domain.Events;
using TicketRush.Domain.Notifications;
using TicketRush.Domain.Orders;
using TicketRush.Domain.Tickets;
using TicketRush.Domain.Users;
using TicketRush.Domain.Waitlist;
using TicketRush.Infrastructure.Identity;
using TicketRush.Infrastructure.Persistence.Seed;

namespace TicketRush.Api.IntegrationTests.Admin;

/// <summary>Spec 16's admin area over HTTP: 404 for everyone else, reasons and audit entries, refunds, moderation, dead letters, flags.</summary>
public sealed class AdminApiTests(ContainersFixture containers) : IAsyncLifetime
{
    private TicketTestWorld _world = null!;
    private WebApplicationFactory<Program> _host = null!;
    private HttpClient _client = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _world = await TicketTestWorld.CreateAsync(containers);
        // Requests get a client address, as behind Kestrel, so audit entries can be checked for it.
        _host = _world.Factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IStartupFilter, FixedClientIp>()));
        _client = _host.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _host.DisposeAsync();
        await _world.DisposeAsync();
    }

    private async Task<string> LoginAsync(string email, string password = DevDataSeeder.DevPassword) =>
        (await LoginResult.ReadAsync(await _client.PostAsJsonAsync("/api/v1/auth/login", new { email, password }, Ct))).AccessToken;

    private Task<string> AdminAsync() => LoginAsync("admin@ticketrush.dev");

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? token, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return _client.SendAsync(request, Ct);
    }

    private async Task<(Guid Id, string Token, string Email)> CustomerAsync()
    {
        var user = await _world.NewUserAsync();
        var email = await _world.WithDbAsync(db => db.Users.Where(u => u.Id == user.Id).Select(u => u.Email!).SingleAsync(Ct));
        return (user.Id, await LoginAsync(email, AuthApi.Password), email);
    }

    [Fact]
    public async Task Every_admin_route_is_404_for_anonymous_customers_and_organizers()
    {
        var customer = (await CustomerAsync()).Token;
        var organizer = await LoginAsync("organizer@ticketrush.dev");
        var id = Guid.NewGuid();
        var routes = new (HttpMethod Method, string Path)[]
        {
            (HttpMethod.Get, "/api/v1/admin/users"), (HttpMethod.Get, $"/api/v1/admin/users/{id}"), (HttpMethod.Post, $"/api/v1/admin/users/{id}/lock"),
            (HttpMethod.Post, $"/api/v1/admin/users/{id}/roles"), (HttpMethod.Get, "/api/v1/admin/organizer-applications"),
            (HttpMethod.Post, $"/api/v1/admin/organizer-applications/{id}/approve"), (HttpMethod.Get, "/api/v1/admin/events"),
            (HttpMethod.Post, $"/api/v1/admin/events/{id}/suspend"), (HttpMethod.Get, "/api/v1/admin/orders"), (HttpMethod.Post, $"/api/v1/admin/orders/{id}/refund"),
            (HttpMethod.Get, "/api/v1/admin/outbox/dead-letters"), (HttpMethod.Post, $"/api/v1/admin/outbox/{id}/retry"), (HttpMethod.Get, "/api/v1/admin/flags"),
            (HttpMethod.Post, $"/api/v1/admin/flags/{id}/lock"), (HttpMethod.Get, "/api/v1/admin/audit"), (HttpMethod.Get, $"/api/v1/admin/audit/{id}"),
        };

        foreach (var token in new string?[] { null, customer, organizer })
        {
            foreach (var (method, path) in routes)
            {
                (await SendAsync(method, path, token, method == HttpMethod.Post ? new { reason = "x" } : null)).StatusCode.ShouldBe(HttpStatusCode.NotFound, $"{method} {path}");
            }
        }

        (await SendAsync(HttpMethod.Get, "/api/v1/admin/users", await AdminAsync())).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Granting_a_role_writes_exactly_one_audit_entry_with_actor_ip_and_correlation_and_an_admin_keeps_their_own_admin_role()
    {
        var admin = await AdminAsync();
        var adminId = await _world.WithDbAsync(db => db.Users.Where(u => u.Email == "admin@ticketrush.dev").Select(u => u.Id).SingleAsync(Ct));
        var (userId, _, _) = await CustomerAsync();
        var before = await _world.WithDbAsync(db => db.AuditEntries.CountAsync(Ct));

        (await SendAsync(HttpMethod.Post, $"/api/v1/admin/users/{userId}/roles", admin, new { role = "GateStaff" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest, "a reason is required");
        var response = await SendAsync(HttpMethod.Post, $"/api/v1/admin/users/{userId}/roles", admin, new { role = "GateStaff", reason = "Nhân viên soát vé mới" });
        var body = await TicketTestWorld.ReadAsync(response);

        body.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ShouldContain("GateStaff");
        var added = await _world.WithDbAsync(db => db.AuditEntries.AsNoTracking().OrderBy(a => a.At).Skip(before).ToListAsync(Ct));
        var entry = added.ShouldHaveSingleItem();
        entry.Action.ShouldBe("role.granted");
        entry.EntityId.ShouldBe(userId.ToString());
        entry.ActorUserId.ShouldBe(adminId);
        entry.ActorIp.ShouldBe(FixedClientIp.Address);
        entry.CorrelationId.ShouldBe(response.Headers.GetValues("X-Correlation-Id").Single());
        entry.Changes!.ShouldContain("Nhân viên soát vé mới");

        (await SendAsync(HttpMethod.Post, $"/api/v1/admin/users/{adminId}/roles/revoke", admin, new { role = "Admin", reason = "thử" })).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await SendAsync(HttpMethod.Post, $"/api/v1/admin/users/{adminId}/lock", admin, new { reason = "thử" })).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var audit = await TicketTestWorld.ReadAsync(await SendAsync(HttpMethod.Get, $"/api/v1/admin/audit?entityId={userId}&action=role.", admin));
        audit.GetProperty("items").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task A_manual_refund_voids_the_tickets_hands_the_seat_to_the_waitlist_and_notifies_the_buyer()
    {
        var admin = await AdminAsync();
        var buyer = await _world.NewUserAsync();
        var ticket = (await _world.IssueSeatedAsync(buyer, TicketTestWorld.SeatedSlug, 1))[0];
        await _world.SetEventAsync(ticket.EventId, startsAt: DateTimeOffset.UtcNow.AddDays(7), endsAt: DateTimeOffset.UtcNow.AddDays(7).AddHours(3));
        await using (var scope = _world.Factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IInventorySeeder>().SeedAsync(ticket.EventId, Ct);
        }

        var waiter = await _world.NewUserAsync();
        await _world.WithDbAsync(db =>
        {
            db.WaitlistEntries.Add(WaitlistEntry.Join(ticket.EventId, waiter.Id, null, 1, DateTimeOffset.UtcNow.AddMinutes(-1)));
            return db.SaveChangesAsync(Ct);
        });

        var path = $"/api/v1/admin/orders/{ticket.OrderId}/refund";
        (await SendAsync(HttpMethod.Post, path, admin, new { reason = "Khách yêu cầu", confirm = "sai" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var refunded = await TicketTestWorld.ReadAsync(await SendAsync(HttpMethod.Post, path, admin, new { reason = "Khách yêu cầu", confirm = ticket.OrderId.ToString() }));
        await _world.Factory.DrainOutboxAsync(Ct);

        refunded.GetProperty("status").GetString().ShouldBe("RefundPending");
        refunded.GetProperty("refundReason").GetString().ShouldBe(RefundReasons.AdminRefund);
        (await _world.WithDbAsync(db => db.Tickets.AsNoTracking().SingleAsync(t => t.Id == ticket.Id, Ct))).Status.ShouldBe(TicketStatus.Voided);
        (await _world.WithDbAsync(db => db.WaitlistEntries.AsNoTracking().SingleAsync(e => e.UserId == waiter.Id, Ct))).Status.ShouldBe(WaitlistStatus.Offered);
        (await _world.WithDbAsync(db => db.Notifications.AsNoTracking().AnyAsync(n => n.UserId == buyer.Id && n.Type == NotificationType.RefundRequested, Ct))).ShouldBeTrue();
        (await _world.WithDbAsync(db => db.AuditEntries.AnyAsync(a => a.Action == "order.refund_manual" && a.EntityId == ticket.OrderId.ToString(), Ct))).ShouldBeTrue();
        (await SendAsync(HttpMethod.Post, path, admin, new { reason = "lần hai", confirm = ticket.OrderId.ToString() })).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Retrying_a_dead_letter_processes_it_once_and_it_can_then_neither_be_retried_nor_discarded()
    {
        var admin = await AdminAsync();
        var seller = await _world.NewUserAsync();
        var ticket = (await _world.IssueSeatedAsync(seller, TicketTestWorld.SeatedSlug, 1))[0];
        var sellerToken = await LoginAsync(await _world.WithDbAsync(db => db.Users.Where(u => u.Id == seller.Id).Select(u => u.Email!).SingleAsync(Ct)), AuthApi.Password);
        await TicketTestWorld.ReadAsync(await SendAsync(HttpMethod.Post, $"/api/v1/tickets/{ticket.Id}/transfers", sellerToken, new { toEmail = "ban@example.com" }));
        await _world.Factory.DrainOutboxAsync(Ct);
        _world.Factory.Emails.Sent.Count(m => m.To == "ban@example.com").ShouldBe(1);

        // The message is marked dead after its email went out (its receipt exists): a retry must not send it again.
        var messageId = await _world.WithDbAsync(db => db.OutboxMessages.Where(m => m.Type == "TransferCreated").Select(m => m.Id).SingleAsync(Ct));
        await _world.WithDbAsync(db => db.Database.ExecuteSqlAsync($"UPDATE outbox_messages SET last_error = 'boom', attempts = 10 WHERE id = {messageId}", Ct));
        var dead = await TicketTestWorld.ReadAsync(await SendAsync(HttpMethod.Get, "/api/v1/admin/outbox/dead-letters", admin));
        dead.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ShouldContain(messageId);

        (await SendAsync(HttpMethod.Post, $"/api/v1/admin/outbox/{messageId}/retry", admin, new { reason = "Đã sửa SMTP" })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await _world.Factory.DrainOutboxAsync(Ct);

        var message = await _world.WithDbAsync(db => db.OutboxMessages.AsNoTracking().SingleAsync(m => m.Id == messageId, Ct));
        message.ProcessedAt.ShouldNotBeNull();
        message.LastError.ShouldBeNull();
        _world.Factory.Emails.Sent.Count(m => m.To == "ban@example.com").ShouldBe(1);
        (await SendAsync(HttpMethod.Post, $"/api/v1/admin/outbox/{messageId}/retry", admin, new { reason = "lại" })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await SendAsync(HttpMethod.Post, $"/api/v1/admin/outbox/{messageId}/discard", admin, new { reason = "bỏ" })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Suspending_hides_the_event_keeps_its_tickets_valid_and_tells_the_organizer_and_restoring_brings_it_back()
    {
        var admin = await AdminAsync();
        var eventId = await _world.EventIdAsync(TicketTestWorld.GaSlug);
        var holder = await _world.NewUserAsync();
        var ticket = (await _world.IssueGaAsync(holder, TicketTestWorld.GaSlug))[0];

        var suspended = await TicketTestWorld.ReadAsync(await SendAsync(HttpMethod.Post, $"/api/v1/admin/events/{eventId}/suspend", admin, new { reason = "Nội dung vi phạm" }));
        await _world.Factory.DrainOutboxAsync(Ct);

        suspended.GetProperty("status").GetString().ShouldBe("Suspended");
        (await _client.GetAsync($"/api/v1/events/{TicketTestWorld.GaSlug}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var holderToken = await LoginAsync(await _world.WithDbAsync(db => db.Users.Where(u => u.Id == holder.Id).Select(u => u.Email!).SingleAsync(Ct)), AuthApi.Password);
        (await SendAsync(HttpMethod.Get, $"/api/v1/tickets/{ticket.Id}/qr-key", holderToken)).StatusCode.ShouldBe(HttpStatusCode.OK, "a sold ticket stays usable");
        var organizerId = await _world.WithDbAsync(db => db.Events.Where(e => e.Id == eventId).Select(e => e.OrganizerId).SingleAsync(Ct));
        (await _world.WithDbAsync(db => db.Notifications.AsNoTracking().SingleAsync(n => n.UserId == organizerId && n.Type == NotificationType.EventModeration, Ct)))
            .Body.ShouldContain("Nội dung vi phạm");
        (await SendAsync(HttpMethod.Post, $"/api/v1/admin/events/{eventId}/suspend", admin, new { reason = "lại" })).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        (await TicketTestWorld.ReadAsync(await SendAsync(HttpMethod.Post, $"/api/v1/admin/events/{eventId}/restore", admin, new { reason = "Đã chỉnh sửa" })))
            .GetProperty("status").GetString().ShouldBe("Published");
        (await _client.GetAsync($"/api/v1/events/{TicketTestWorld.GaSlug}", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_approved_application_makes_the_user_an_organizer_and_emails_them()
    {
        var admin = await AdminAsync();
        var (userId, token, email) = await CustomerAsync();

        (await SendAsync(HttpMethod.Post, "/api/v1/organizer-applications", token, new { companyName = "", phone = "0901234567" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var application = await TicketTestWorld.ReadAsync(
            await SendAsync(HttpMethod.Post, "/api/v1/organizer-applications", token, new { companyName = "Công ty Sự Kiện Việt", phone = "0901234567", description = "Tổ chức hoà nhạc" }));
        (await SendAsync(HttpMethod.Post, "/api/v1/organizer-applications", token, new { companyName = "Lần hai", phone = "0901234567" })).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var id = application.GetProperty("id").GetGuid();

        var approved = await TicketTestWorld.ReadAsync(await SendAsync(HttpMethod.Post, $"/api/v1/admin/organizer-applications/{id}/approve", admin, new { reason = "Hồ sơ đầy đủ" }));
        await _world.Factory.DrainOutboxAsync(Ct);

        approved.GetProperty("status").GetString().ShouldBe("Approved");
        await using (var scope = _world.Factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            (await users.IsInRoleAsync((await users.FindByIdAsync(userId.ToString()))!, Roles.Organizer)).ShouldBeTrue();
        }

        (await _world.WithDbAsync(db => db.Notifications.AsNoTracking().AnyAsync(n => n.UserId == userId && n.Type == NotificationType.OrganizerApplication, Ct))).ShouldBeTrue();
        (await TicketTestWorld.ReadAsync(await SendAsync(HttpMethod.Get, "/api/v1/me/organizer-application", token))).GetProperty("status").GetString().ShouldBe("Approved");
        email.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task A_flag_can_be_dismissed_or_used_to_lock_the_account_which_ends_its_sessions()
    {
        var admin = await AdminAsync();
        var (userId, token, _) = await CustomerAsync();
        var dismissed = AccountFlag.Raise(userId, AccountFlag.SharedPhone, "4 accounts", DateTimeOffset.UtcNow);
        var locking = AccountFlag.Raise(userId, AccountFlag.SubnetJoinBurst, "25 joins", DateTimeOffset.UtcNow);
        await _world.WithDbAsync(db =>
        {
            db.AccountFlags.AddRange(dismissed, locking);
            return db.SaveChangesAsync(Ct);
        });

        (await TicketTestWorld.ReadAsync(await SendAsync(HttpMethod.Get, "/api/v1/admin/flags", admin))).GetProperty("items").GetArrayLength().ShouldBe(2);
        (await SendAsync(HttpMethod.Post, $"/api/v1/admin/flags/{dismissed.Id}/dismiss", admin, new { reason = "Gia đình dùng chung số" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SendAsync(HttpMethod.Post, $"/api/v1/admin/flags/{locking.Id}/lock", admin, new { reason = "Bot" })).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await TicketTestWorld.ReadAsync(await SendAsync(HttpMethod.Get, "/api/v1/admin/flags", admin))).GetProperty("items").GetArrayLength().ShouldBe(0);
        (await TicketTestWorld.ReadAsync(await SendAsync(HttpMethod.Get, $"/api/v1/admin/users/{userId}", admin))).GetProperty("user").GetProperty("locked").GetBoolean().ShouldBeTrue();
        (await _world.WithDbAsync(db => db.RefreshTokens.CountAsync(t => t.UserId == userId && t.RevokedAt == null, Ct))).ShouldBe(0);
        token.ShouldNotBeEmpty();
    }
}

/// <summary>Gives every request a client address (TestServer has none), as Kestrel would.</summary>
internal sealed class FixedClientIp : IStartupFilter
{
    public const string Address = "203.0.113.7";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, call) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(Address);
            return call(context);
        });
        next(app);
    };
}
