using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using TicketRush.Api.IntegrationTests.Auth;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Api.IntegrationTests.Tickets;
using TicketRush.Application.Abstractions.Dashboard;
using TicketRush.Application.Abstractions.Messaging;
using TicketRush.Application.Features.Dashboard;
using TicketRush.Domain.Events;
using TicketRush.Domain.Orders;
using TicketRush.Domain.Primitives;
using TicketRush.Domain.Users;
using TicketRush.Infrastructure.Dashboard;
using TicketRush.Infrastructure.Identity;
using TicketRush.Infrastructure.Persistence;
using TicketRush.Infrastructure.Persistence.Seed;
using TicketRush.Infrastructure.Redis;
using Order = TicketRush.Domain.Orders.Order;

namespace TicketRush.Api.IntegrationTests.Dashboard;

/// <summary>
/// Spec 15 acceptance against Postgres and Redis: counters counted once per paid order (primary sales only), rebuilt from Postgres
/// after a flush, one snapshot per tick whatever the sale rate, ownership on routes and hub, series and orders paging, and the
/// attendee CSV streamed with flat memory.
/// </summary>
public sealed class DashboardTests(ContainersFixture containers) : IAsyncLifetime
{
    private const string Slug = TicketTestWorld.SeatedSlug;
    private TicketTestWorld _world = null!;
    private Guid _eventId;
    private (Guid Id, long Price) _seated;
    private (Guid Id, long Price) _ga;
    private List<int> _seats = [];
    private int _nextSeat;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _world = await TicketTestWorld.CreateAsync(containers);
        _eventId = await _world.EventIdAsync(Slug);
        await _world.SetEventAsync(_eventId, startsAt: DateTimeOffset.UtcNow.AddDays(5), endsAt: DateTimeOffset.UtcNow.AddDays(5).AddHours(3));
        await _world.WithDbAsync(async db =>
        {
            var tiers = await db.Events.AsNoTracking().Where(e => e.Id == _eventId).SelectMany(e => e.Tiers).ToListAsync(Ct);
            var seated = tiers.First(t => t.Kind == TicketKind.Seated);
            var ga = tiers.First(t => t.Kind == TicketKind.GeneralAdmission);
            _seated = (seated.Id, seated.Price.Amount);
            _ga = (ga.Id, ga.Price.Amount);
            _seats = await db.EventSeats.AsNoTracking().Where(s => s.EventId == _eventId && s.TierId == seated.Id).OrderBy(s => s.Index).Select(s => s.Index).Take(50).ToListAsync(Ct);
            return 0;
        });
    }

    public async ValueTask DisposeAsync() => await _world.DisposeAsync();

    private IDatabase Redis => _world.Factory.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase();

    private IDashboardStore Store => _world.Factory.Services.GetRequiredService<IDashboardStore>();

    /// <summary>A paid primary order raised through the domain, so its <c>OrderPaid</c> goes through the outbox like a real one.</summary>
    private async Task<Order> PaidAsync(int seats = 1, int ga = 0, OrderKind kind = OrderKind.Primary, string buyer = "Nguyễn Văn An")
    {
        var now = DateTimeOffset.UtcNow;
        var items = new List<OrderItem>();
        for (var i = 0; i < seats; i++)
        {
            items.Add(new OrderItem(_seated.Id, _seats[_nextSeat++], new Money(_seated.Price), 1));
        }

        if (ga > 0)
        {
            items.Add(new OrderItem(_ga.Id, null, new Money(_ga.Price), ga));
        }

        var order = kind == OrderKind.Resale
            ? Order.CreateResale(Guid.CreateVersion7(), _eventId, Guid.CreateVersion7(), items[0], new BuyerInfo(buyer, "an@example.com", null), now.AddMinutes(8), now)
            : Order.Create(Guid.CreateVersion7(), _eventId, Guid.CreateVersion7(), items, items.Aggregate(Money.Zero(), (sum, i) => sum + i.LineTotal()),
                new BuyerInfo(buyer, "an@example.com", null), now.AddMinutes(8), now);
        order.StartPayment(now);
        order.MarkPaid(now);
        await _world.WithDbAsync(db =>
        {
            db.Orders.Add(order);
            return db.SaveChangesAsync(Ct);
        });
        return order;
    }

    private async Task<DashboardSnapshot> SnapshotAsync()
    {
        await using var scope = _world.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DashboardSnapshotBuilder>().BuildAsync(_eventId, Ct);
    }

    private async Task<TicketTestWorld.User> OrganizerAsync(string? email = null)
    {
        var password = DevDataSeeder.DevPassword;
        if (email is null)
        {
            email = $"org-{Guid.NewGuid():N}@example.com";
            await using var scope = _world.Factory.Services.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = new AppUser { Id = Guid.CreateVersion7(), Email = email, UserName = email, FullName = "Ban Tổ Chức Khác", EmailConfirmed = true };
            (await users.CreateAsync(user, AuthApi.Password)).Succeeded.ShouldBeTrue();
            password = AuthApi.Password;
            (await users.AddToRoleAsync(user, Roles.Organizer)).Succeeded.ShouldBeTrue();
        }

        var login = await LoginResult.ReadAsync(await _world.Auth.LoginAsync(email, password));
        return new TicketTestWorld.User(Guid.Empty, login.AccessToken, email);
    }

    private Task<TicketTestWorld.User> OwnerAsync() => OrganizerAsync("organizer@ticketrush.dev");

    private Task<HttpResponseMessage> GetAsync(string path, TicketTestWorld.User who) => _world.GetAsync($"/api/v1/organizer/events/{_eventId}/{path}", who);

    private HubConnection Connect(string token) => new HubConnectionBuilder()
        .WithUrl(new Uri(_world.Factory.Server.BaseAddress, "/hubs/organizer"), options =>
        {
            options.HttpMessageHandlerFactory = _ => _world.Factory.Server.CreateHandler();
            options.Transports = HttpTransportType.LongPolling;
            options.AccessTokenProvider = () => Task.FromResult<string?>(token);
        })
        .Build();

    [Fact]
    public async Task A_paid_order_is_counted_once_per_tier_and_minute_even_when_redelivered_and_resale_is_left_out()
    {
        var order = await PaidAsync(seats: 2, ga: 3);
        await _world.Factory.DrainOutboxAsync(Ct);
        var resale = await PaidAsync(kind: OrderKind.Resale);
        await _world.Factory.DrainOutboxAsync(Ct);

        var snapshot = await SnapshotAsync();
        snapshot.Sold.ShouldBe(5);
        snapshot.Revenue.ShouldBe(order.Total.Amount);
        snapshot.Funnel.Paid.ShouldBe(1);
        snapshot.Tiers.Single(t => t.TierId == _seated.Id).Sold.ShouldBe(2);
        snapshot.Tiers.Single(t => t.TierId == _ga.Id).Revenue.ShouldBe(3 * _ga.Price);
        snapshot.RecentOrders.ShouldHaveSingleItem().OrderId.ShouldBe(order.Id);
        snapshot.RecentOrders[0].BuyerName.ShouldBe("Nguyễn Văn An");
        var minutes = await _world.WithDbAsync(db => db.SalesPerMinute.AsNoTracking().Where(s => s.EventId == _eventId).ToListAsync(Ct));
        minutes.Sum(m => m.Tickets).ShouldBe(5);
        minutes.Sum(m => m.Revenue).ShouldBe(order.Total.Amount);

        // A redelivery between the script and the outbox commit: the marker stops the second count.
        (await Store.RecordPaidAsync(_eventId, new RecentOrder(order.Id, "x", 5, order.Total.Amount, order.PaidAt!.Value), [new PaidTierLine(_seated.Id, 2, 1)], Ct)).ShouldBeFalse();
        (await SnapshotAsync()).Sold.ShouldBe(5);
        resale.Kind.ShouldBe(OrderKind.Resale);
    }

    [Fact]
    public async Task After_the_counters_are_flushed_the_reconciler_restores_them_and_revenue_equals_the_paid_orders_sum()
    {
        for (var i = 0; i < 6; i++)
        {
            await PaidAsync(seats: 1, ga: i % 3);
        }

        await _world.Factory.DrainOutboxAsync(Ct);
        await Store.RecordHeldAsync(_eventId, Ct);
        var keys = _world.Factory.Services.GetRequiredService<RedisKeys>();
        foreach (var key in new[] { "", ":recent" })
        {
            await Redis.KeyDeleteAsync($"{keys.Prefix}dash:{{{_eventId:N}}}{key}");
        }

        (await SnapshotAsync()).Sold.ShouldBe(0);
        await using (var scope = _world.Factory.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<DashboardReconcileHandler>().HandleAsync(Ct)).ShouldBeGreaterThan(0);
        }

        var snapshot = await SnapshotAsync();
        var truth = await _world.WithDbAsync(db => db.Orders.AsNoTracking()
            .Where(o => o.EventId == _eventId && o.Status == OrderStatus.Paid && o.Kind == OrderKind.Primary).SumAsync(o => o.Total.Amount, Ct));
        snapshot.Revenue.ShouldBe(truth);
        snapshot.Sold.ShouldBe(6 + 0 + 1 + 2 + 0 + 1 + 2);
        snapshot.Funnel.Paid.ShouldBe(6);
        snapshot.Funnel.OrdersCreated.ShouldBe(6);
        snapshot.RecentOrders.Count.ShouldBe(6);
        snapshot.Tiers.Sum(t => t.Revenue).ShouldBe(truth);
    }

    [Fact]
    public async Task Five_hundred_sales_between_two_ticks_still_send_exactly_one_snapshot_per_watched_event_and_tick()
    {
        var publisher = new CapturingPublisher();
        await Store.AddViewerAsync(_eventId, "conn-1", DateTimeOffset.UtcNow, Ct);

        await Parallel.ForEachAsync(Enumerable.Range(0, 500), Ct, async (i, token) =>
            await Store.RecordPaidAsync(_eventId, new RecentOrder(Guid.NewGuid(), $"Khách {i}", 1, _ga.Price, DateTimeOffset.UtcNow), [new PaidTierLine(_ga.Id, 1, _ga.Price)], token));
        (await TickAsync(publisher)).ShouldBe(1);
        await Parallel.ForEachAsync(Enumerable.Range(0, 500), Ct, async (i, token) =>
            await Store.RecordPaidAsync(_eventId, new RecentOrder(Guid.NewGuid(), $"Khách {i}", 1, _ga.Price, DateTimeOffset.UtcNow), [new PaidTierLine(_ga.Id, 1, _ga.Price)], token));
        (await TickAsync(publisher)).ShouldBe(1);

        publisher.Sent.Count.ShouldBe(2);
        publisher.Sent[0].Sold.ShouldBe(500);
        publisher.Sent[1].Sold.ShouldBe(1000);
        publisher.Sent[1].RecentOrders.Count.ShouldBe(20);

        // Nobody watching (the viewer's heartbeat stopped): nothing is sent.
        await Store.RemoveViewerAsync(_eventId, "conn-1", Ct);
        (await TickAsync(publisher)).ShouldBe(0);
    }

    private async Task<int> TickAsync(CapturingPublisher publisher)
    {
        await using var scope = _world.Factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var handler = new DashboardBroadcastHandler(
            services.GetRequiredService<AppDbContext>(), Store, services.GetRequiredService<DashboardSnapshotBuilder>(), publisher, TimeProvider.System,
            NullLogger<DashboardBroadcastHandler>.Instance);
        return await handler.HandleAsync(Ct);
    }

    private sealed class CapturingPublisher : IDashboardPublisher
    {
        public List<DashboardSnapshot> Sent { get; } = [];

        public Task PublishAsync(DashboardSnapshot snapshot, CancellationToken ct)
        {
            Sent.Add(snapshot);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Another_organizer_gets_404_on_every_route_and_cannot_join_the_hub_group()
    {
        var stranger = await OrganizerAsync();
        foreach (var path in new[] { "dashboard", "sales-series", "orders", "export.csv" })
        {
            (await GetAsync(path, stranger)).StatusCode.ShouldBe(HttpStatusCode.NotFound, path);
        }

        var customer = await _world.NewUserAsync();
        (await GetAsync("dashboard", customer)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        await using var strangerHub = Connect(stranger.Access);
        await strangerHub.StartAsync(Ct);
        (await Should.ThrowAsync<HubException>(() => strangerHub.InvokeAsync("JoinDashboard", _eventId, Ct))).Message.ShouldContain("dashboard.not_found");

        var owner = await OwnerAsync();
        await using var ownerHub = Connect(owner.Access);
        var received = new TaskCompletionSource<DashboardSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        ownerHub.On<DashboardSnapshot>("DashboardSnapshot", s => received.TrySetResult(s));
        await ownerHub.StartAsync(Ct);
        await ownerHub.InvokeAsync("JoinDashboard", _eventId, Ct);
        (await Store.WatchedEventsAsync(DateTimeOffset.UtcNow.AddMinutes(-1), Ct)).ShouldContain(_eventId);

        await using (var scope = _world.Factory.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<DashboardBroadcastHandler>().HandleAsync(Ct)).ShouldBe(1);
        }

        (await received.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct)).EventId.ShouldBe(_eventId);
    }

    [Fact]
    public async Task The_owner_reads_the_snapshot_series_by_minute_and_hour_and_pages_the_paid_orders()
    {
        var owner = await OwnerAsync();
        var orders = new List<Order>();
        for (var i = 0; i < 3; i++)
        {
            orders.Add(await PaidAsync(seats: 1, buyer: $"Khách {i}"));
        }

        await _world.Factory.DrainOutboxAsync(Ct);

        var snapshot = await TicketTestWorld.ReadAsync(await GetAsync("dashboard", owner));
        snapshot.GetProperty("sold").GetInt64().ShouldBe(3);
        snapshot.GetProperty("tiers").GetArrayLength().ShouldBeGreaterThan(1);

        var minutes = await TicketTestWorld.ReadAsync(await GetAsync("sales-series?bucket=1m", owner));
        minutes.GetProperty("points").EnumerateArray().Sum(p => p.GetProperty("tickets").GetInt32()).ShouldBe(3);
        var hours = await TicketTestWorld.ReadAsync(await GetAsync("sales-series?bucket=1h", owner));
        hours.GetProperty("points").EnumerateArray().Single().GetProperty("tickets").GetInt32().ShouldBe(3);
        (await GetAsync("sales-series?bucket=5m", owner)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var first = await TicketTestWorld.ReadAsync(await GetAsync("orders?limit=2", owner));
        first.GetProperty("items").GetArrayLength().ShouldBe(2);
        var cursor = first.GetProperty("nextCursor").GetString()!;
        var second = await TicketTestWorld.ReadAsync(await GetAsync($"orders?limit=2&cursor={Uri.EscapeDataString(cursor)}", owner));
        second.GetProperty("items").GetArrayLength().ShouldBe(1);
        second.GetProperty("nextCursor").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
        first.GetProperty("items").EnumerateArray().Concat(second.GetProperty("items").EnumerateArray())
            .Select(o => o.GetProperty("orderId").GetGuid()).ShouldBe(orders.Select(o => o.Id), ignoreOrder: true);
    }

    [Fact]
    public async Task The_csv_lists_the_live_tickets_with_buyer_holder_seat_and_check_in()
    {
        var owner = await OwnerAsync();
        var holder = await _world.NewUserAsync("=Người Giữ, \"Vé\"");
        var ticket = (await _world.IssueSeatedAsync(holder, Slug, 1))[0];
        await _world.SetTicketStatusAsync(ticket.Id, TicketRush.Domain.Tickets.TicketStatus.CheckedIn);

        var response = await GetAsync("export.csv", owner);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/csv");
        var bytes = await response.Content.ReadAsByteArrayAsync(Ct);
        bytes.Take(3).ShouldBe(new byte[] { 0xEF, 0xBB, 0xBF });
        var lines = Encoding.UTF8.GetString(bytes[3..]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(l => l.Split('\n', StringSplitOptions.RemoveEmptyEntries)).ToList();
        lines[0].ShouldStartWith("Mã đơn,Người mua");
        lines.Count.ShouldBe(2);
        lines[1].ShouldContain(ticket.OrderId.ToString());
        lines[1].ShouldContain("\"'=Người Giữ, \"\"Vé\"\"\"");
        lines[1].ShouldContain("Đã check-in");
    }

    [Fact]
    public async Task Exporting_100000_attendees_streams_with_flat_memory()
    {
        var owner = await OwnerAsync();
        var order = await PaidAsync(seats: 1);
        var holder = await _world.NewUserAsync();
        await _world.WithDbAsync(db => db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO tickets (id, order_id, event_id, tier_id, seat_index, owner_user_id, status, qr_secret, created_at, updated_at)
            SELECT gen_random_uuid(), {order.Id}, {_eventId}, {_ga.Id}, NULL, {holder.Id}, 'Valid', '\x00'::bytea, now(), now()
            FROM generate_series(1, 100000)
            """, Ct));

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/organizer/events/{_eventId}/export.csv");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", owner.Access);
        var before = GC.GetTotalMemory(forceFullCollection: true);
        using var response = await _world.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var body = await response.Content.ReadAsStreamAsync(Ct);
        using var reader = new StreamReader(body, Encoding.UTF8);
        long peak = 0, lines = 0;
        while (await reader.ReadLineAsync(Ct) is not null)
        {
            if (++lines % 20_000 == 0)
            {
                peak = Math.Max(peak, GC.GetTotalMemory(forceFullCollection: true) - before);
            }
        }

        lines.ShouldBe(100_000 + 1);
        // The whole CSV is about 15 MB and 100,000 materialized rows would retain far more; streaming keeps a few buffers.
        peak.ShouldBeLessThan(10 * 1024 * 1024);
    }
}
