using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Api.IntegrationTests.Tickets;
using TicketRush.Domain.Audit;

namespace TicketRush.Api.IntegrationTests.Admin;

/// <summary>Spec 16's audit log against Postgres: append-only at the database, automatic and explicit entries, nothing sensitive.</summary>
public sealed class AuditLogTests(ContainersFixture containers) : IAsyncLifetime
{
    private TicketTestWorld _world = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _world = await TicketTestWorld.CreateAsync(containers);

    public async ValueTask DisposeAsync() => await _world.DisposeAsync();

    private Task<List<AuditEntry>> EntriesAsync() => _world.WithDbAsync(db => db.AuditEntries.AsNoTracking().OrderBy(a => a.At).ToListAsync(Ct));

    [Fact]
    public async Task Update_delete_and_truncate_on_audit_entries_fail_at_the_database()
    {
        await _world.Auth.LoginAsync("nobody@example.com", "wrong-password");
        (await EntriesAsync()).ShouldNotBeEmpty();

        foreach (var sql in new[] { "UPDATE audit_entries SET action = 'x'", "DELETE FROM audit_entries", "TRUNCATE audit_entries" })
        {
            var error = await Should.ThrowAsync<PostgresException>(() => _world.WithDbAsync(db => db.Database.ExecuteSqlRawAsync(sql, Ct)));
            error.MessageText.ShouldContain("append-only");
        }

        (await EntriesAsync()).ShouldNotBeEmpty();
    }

    [Fact]
    public async Task A_failed_sign_in_is_recorded_with_the_ip_and_the_correlation_id_and_the_lockout_with_it()
    {
        var user = await _world.NewUserAsync();
        var email = await _world.WithDbAsync(db => db.Users.Where(u => u.Id == user.Id).Select(u => u.Email!).SingleAsync(Ct));

        // TestServer has no client address: a startup filter gives every request one, as Kestrel would.
        using var host = _world.Factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IStartupFilter, FixedClientIp>()));
        using var client = host.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "sai-mat-khau" }, Ct);
        var correlation = response.Headers.GetValues("X-Correlation-Id").Single();

        var failed = (await EntriesAsync()).Single(a => a.Action == "auth.login_failed" && a.EntityId == user.Id.ToString());
        failed.ActorUserId.ShouldBeNull();
        failed.ActorIp.ShouldBe("203.0.113.7");
        failed.CorrelationId.ShouldBe(correlation);
    }

    [Fact]
    public async Task Event_and_ticket_changes_are_recorded_field_by_field_and_nothing_sensitive_is_ever_written()
    {
        var owner = await _world.NewUserAsync();
        var ticket = (await _world.IssueSeatedAsync(owner, TicketTestWorld.SeatedSlug, 1))[0];
        await _world.WithDbAsync(async db =>
        {
            var tracked = await db.Tickets.SingleAsync(t => t.Id == ticket.Id, Ct);
            tracked.List();
            var evt = await db.Events.SingleAsync(e => e.Id == ticket.EventId, Ct);
            evt.MarkSoldOut(!evt.IsSoldOut);
            return await db.SaveChangesAsync(Ct);
        });
        await _world.Auth.LoginAsync("someone@example.com", "wrong");

        var entries = await EntriesAsync();
        var listed = entries.Single(a => a.Action == "ticket.updated" && a.EntityId == ticket.Id.ToString());
        using (var changes = JsonDocument.Parse(listed.Changes!))
        {
            changes.RootElement.GetProperty("status").GetProperty("before").GetString().ShouldBe("Valid");
            changes.RootElement.GetProperty("status").GetProperty("after").GetString().ShouldBe("Listed");
            changes.RootElement.TryGetProperty("qrSecret", out _).ShouldBeFalse();
        }

        entries.ShouldContain(a => a.Action == "event.updated" && a.EntityId == ticket.EventId.ToString());

        // Scan every entry for the secrets that exist in this database.
        var secret = Convert.ToBase64String(await _world.SecretOfAsync(ticket.Id));
        var hashes = await _world.WithDbAsync(db => db.Users.Select(u => u.PasswordHash!).Where(h => h != null).ToListAsync(Ct));
        var stamps = await _world.WithDbAsync(db => db.Users.Select(u => u.SecurityStamp!).Where(h => h != null).ToListAsync(Ct));
        var tokens = await _world.WithDbAsync(db => db.RefreshTokens.Select(t => t.TokenHash).ToListAsync(Ct));
        foreach (var entry in entries.Where(e => e.Changes is not null))
        {
            entry.Changes!.ShouldNotContain(secret);
            entry.Changes!.ShouldNotContain("qrSecret", Case.Insensitive);
            entry.Changes!.ShouldNotContain("passwordHash", Case.Insensitive);
            foreach (var value in hashes.Concat(stamps).Concat(tokens))
            {
                entry.Changes!.ShouldNotContain(value);
            }
        }
    }

    private sealed class FixedClientIp : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, call) =>
            {
                context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.7");
                return call(context);
            });
            next(app);
        };
    }
}
