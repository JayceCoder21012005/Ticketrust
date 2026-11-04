using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using TicketRush.Api.IntegrationTests.Auth;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Application.Abstractions.Inventory;
using TicketRush.Domain.Events;
using TicketRush.Domain.Orders;
using TicketRush.Domain.Primitives;
using TicketRush.Domain.Tickets;
using TicketRush.Infrastructure.Catalog;
using TicketRush.Infrastructure.Identity;
using TicketRush.Infrastructure.Persistence;
using TicketRush.Infrastructure.Persistence.Seed;

namespace TicketRush.Api.IntegrationTests.Orders;

/// <summary>Spec 08 order endpoints (create, read, cancel, my orders) over HTTP against the dev seed.</summary>
public sealed class OrderApiTests(ContainersFixture containers) : IAsyncLifetime
{
    private ApiFactory _factory = null!;
    private HttpClient _client = null!;
    private AuthApi _auth = null!;
    private Guid _event;
    private Guid[] _gaTiers = [];
    private Guid _gaTier;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _factory = new ApiFactory(await containers.CreateDatabaseAsync(), containers.RedisConnectionString);
        _client = AuthApi.CreateClient(_factory);
        _auth = new AuthApi(_factory, _client);
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DevDataSeeder>().SeedAsync(Ct);
        var evt = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Events.AsNoTracking().Include(e => e.Tiers)
            .SingleAsync(e => e.Slug == "son-tung-mtp-live", Ct);
        _event = evt.Id;
        _gaTiers = [.. evt.Tiers.Where(t => t.Kind == TicketKind.GeneralAdmission).Select(t => t.Id)];
        _gaTier = _gaTiers[0];
        await scope.ServiceProvider.GetRequiredService<IInventorySeeder>().SeedAsync(_event, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private sealed record Buyer(Guid UserId, string Access);

    private async Task<Buyer> NewBuyerAsync()
    {
        var email = $"buyer-{Guid.NewGuid():N}@example.com";
        var access = (await _auth.SignInWithRoleAsync(email, "Customer")).AccessToken;
        using var scope = _factory.Services.CreateScope();
        var user = await scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>().FindByEmailAsync(email);
        return new Buyer(user!.Id, access);
    }

    private async Task<ActiveHold> HoldAsync(Buyer buyer, int[]? seats = null, int ga = 0, TimeSpan? ttl = null, bool replace = false, int maxTickets = 8)
    {
        var request = new HoldRequest(
            _event, buyer.UserId, seats ?? [], ga > 0 ? [new GaLine(_gaTier, ga)] : [], _gaTiers, maxTickets, replace, DateTimeOffset.UtcNow, ttl ?? TimeSpan.FromMinutes(8));
        var outcome = await Store.CreateHoldAsync(request, Ct);
        outcome.Status.ShouldBe(HoldStatus.Ok);
        return (await Store.GetUserHoldAsync(_event, buyer.UserId, Ct))!;
    }

    private IInventoryStore Store => _factory.Services.GetRequiredService<IInventoryStore>();

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? access, object? body = null, string? key = null, HttpClient? client = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (access is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        }

        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        return (client ?? _client).SendAsync(request, Ct);
    }

    private static object CreateBody(Guid eventId, Guid holdId, string name = "Nguyễn Văn An", string email = "an@example.com", string? phone = "0901234567") =>
        new { eventId, holdId, buyer = new { name, email, phone } };

    private Task<HttpResponseMessage> CreateAsync(Buyer buyer, Guid holdId, string? key = null, object? body = null, HttpClient? client = null) =>
        SendAsync(HttpMethod.Post, "/api/v1/orders", buyer.Access, body ?? CreateBody(_event, holdId), key ?? Guid.NewGuid().ToString(), client);

    private Task<HttpResponseMessage> CancelAsync(Buyer buyer, Guid orderId, string? key = null, HttpClient? client = null) =>
        SendAsync(HttpMethod.Post, $"/api/v1/orders/{orderId}/cancel", buyer.Access, key: key ?? Guid.NewGuid().ToString(), client: client);

    private Task<HttpResponseMessage> GetOrderAsync(Buyer? buyer, Guid orderId) =>
        SendAsync(HttpMethod.Get, $"/api/v1/orders/{orderId}", buyer?.Access);

    private async Task<JsonElement> CreateOrderAsync(Buyer buyer, ActiveHold hold) =>
        await JsonAsync(await CreateAsync(buyer, hold.HoldId), HttpStatusCode.Created);

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode status = HttpStatusCode.OK)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(status, text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task ShouldFailWithAsync(HttpResponseMessage response, HttpStatusCode status, string code) =>
        (await JsonAsync(response, status)).GetProperty("code").GetString().ShouldBe(code);

    private async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private Task<int> OrderCountAsync() => WithDbAsync(db => db.Orders.CountAsync(Ct));

    private async Task SetStatusAsync(Guid orderId, OrderStatus target) => await WithDbAsync(async db =>
    {
        var order = await db.Orders.SingleAsync(o => o.Id == orderId, Ct);
        var now = DateTimeOffset.UtcNow;
        switch (target)
        {
            case OrderStatus.AwaitingPayment:
                order.StartPayment(now);
                break;
            case OrderStatus.PaymentFailed:
                order.StartPayment(now);
                order.MarkPaymentFailed();
                break;
            case OrderStatus.Paid:
                order.StartPayment(now);
                order.MarkPaid(now);
                break;
            case OrderStatus.Expired:
                order.Expire();
                break;
            case OrderStatus.RefundPending:
                order.StartPayment(now);
                order.MarkPaid(now);
                order.RequestRefundForCancelledEvent();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(target));
        }

        await db.SaveChangesAsync(Ct);
        return 0;
    });

    private Task<int> SetEventStatusAsync(string status) =>
        WithDbAsync(db => db.Database.ExecuteSqlAsync($"UPDATE events SET status = {status} WHERE id = {_event}", Ct));

    // ---- POST /orders ----

    [Fact]
    public async Task Creating_an_order_copies_items_and_prices_from_the_hold()
    {
        var buyer = await NewBuyerAsync();
        var hold = await HoldAsync(buyer, seats: [3, 1], ga: 2);
        var held = await JsonAsync(await SendAsync(HttpMethod.Get, $"/api/v1/events/{_event}/holds/me", buyer.Access));

        var response = await CreateAsync(buyer, hold.HoldId);
        var order = await JsonAsync(response, HttpStatusCode.Created);

        response.Headers.Location!.ToString().ShouldBe($"/api/v1/orders/{order.GetProperty("id").GetGuid()}");
        order.GetProperty("status").GetString().ShouldBe("Pending");
        order.GetProperty("eventId").GetGuid().ShouldBe(_event);
        order.GetProperty("total").GetInt64().ShouldBe(held.GetProperty("total").GetInt64());
        order.GetProperty("seats").EnumerateArray().Select(s => (s.GetProperty("index").GetInt32(), s.GetProperty("price").GetInt64()))
            .ShouldBe(held.GetProperty("seats").EnumerateArray().Select(s => (s.GetProperty("index").GetInt32(), s.GetProperty("price").GetInt64())));
        order.GetProperty("seats")[0].GetProperty("section").GetString().ShouldBe(held.GetProperty("seats")[0].GetProperty("section").GetString());
        var ga = order.GetProperty("ga").EnumerateArray().Single();
        ga.GetProperty("quantity").GetInt32().ShouldBe(2);
        ga.GetProperty("price").GetInt64().ShouldBe(held.GetProperty("ga")[0].GetProperty("price").GetInt64());
        ga.GetProperty("name").GetString().ShouldBe(held.GetProperty("ga")[0].GetProperty("name").GetString());
        order.GetProperty("expiresAt").GetDateTimeOffset().ShouldBe(held.GetProperty("expiresAt").GetDateTimeOffset());
        order.GetProperty("serverNow").GetDateTimeOffset().ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-1));
        order.GetProperty("buyer").GetProperty("name").GetString().ShouldBe("Nguyễn Văn An");
        order.GetProperty("buyer").GetProperty("email").GetString().ShouldBe("an@example.com");
        order.GetProperty("buyer").GetProperty("phone").GetString().ShouldBe("0901234567");
        order.GetProperty("paidAt").ValueKind.ShouldBe(JsonValueKind.Null);
        order.GetProperty("ticketCount").ValueKind.ShouldBe(JsonValueKind.Null);
        (await OrderCountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Without_a_hold_the_order_is_hold_invalid()
    {
        var buyer = await NewBuyerAsync();

        await ShouldFailWithAsync(await CreateAsync(buyer, Guid.NewGuid()), HttpStatusCode.Conflict, "order.hold_invalid");

        (await OrderCountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Another_buyers_hold_id_is_hold_invalid()
    {
        var owner = await NewBuyerAsync();
        var thief = await NewBuyerAsync();
        var ownersHold = await HoldAsync(owner, seats: [5]);
        await HoldAsync(thief, seats: [6]);

        await ShouldFailWithAsync(await CreateAsync(thief, ownersHold.HoldId), HttpStatusCode.Conflict, "order.hold_invalid");
        await ShouldFailWithAsync(await CreateAsync(await NewBuyerAsync(), ownersHold.HoldId), HttpStatusCode.Conflict, "order.hold_invalid");

        (await OrderCountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task A_hold_id_that_is_not_the_current_hold_is_hold_invalid()
    {
        var buyer = await NewBuyerAsync();
        var old = await HoldAsync(buyer, seats: [7]);
        await HoldAsync(buyer, seats: [8], replace: true);

        await ShouldFailWithAsync(await CreateAsync(buyer, old.HoldId), HttpStatusCode.Conflict, "order.hold_invalid");
    }

    [Fact]
    public async Task An_expired_hold_is_hold_expired()
    {
        var buyer = await NewBuyerAsync();
        var hold = await HoldAsync(buyer, seats: [9], ttl: TimeSpan.FromSeconds(1));
        await Task.Delay(TimeSpan.FromSeconds(1.5), Ct);

        await ShouldFailWithAsync(await CreateAsync(buyer, hold.HoldId), HttpStatusCode.Conflict, "order.hold_expired");

        (await OrderCountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task A_released_hold_that_already_had_an_order_is_hold_expired()
    {
        var buyer = await NewBuyerAsync();
        var hold = await HoldAsync(buyer, seats: [10]);
        await CreateOrderAsync(buyer, hold);
        await Store.ReleaseHoldAsync(_event, hold.HoldId, _gaTiers, Ct);

        await ShouldFailWithAsync(await CreateAsync(buyer, hold.HoldId), HttpStatusCode.Conflict, "order.hold_expired");
    }

    [Theory]
    [InlineData("Cancelled")]
    [InlineData("Draft")]
    public async Task An_event_that_is_not_published_in_the_database_rejects_the_order_even_if_the_cache_and_hold_survived(string status)
    {
        var buyer = await NewBuyerAsync();
        var hold = await HoldAsync(buyer, seats: [11]);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            // The cancel raced the order: the catalog cache and the Redis hold still say "on sale".
            (await scope.ServiceProvider.GetRequiredService<IHoldCatalog>().GetAsync(_event, Ct)).ShouldNotBeNull();
        }

        await SetEventStatusAsync(status);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<IHoldCatalog>().GetAsync(_event, Ct)).ShouldNotBeNull("the cache is still stale");
        }

        (await Store.GetUserHoldAsync(_event, buyer.UserId, Ct)).ShouldNotBeNull();
        await ShouldFailWithAsync(await CreateAsync(buyer, hold.HoldId), HttpStatusCode.NotFound, "event.not_found");

        (await OrderCountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task An_unknown_event_is_404()
    {
        var buyer = await NewBuyerAsync();
        var hold = await HoldAsync(buyer, seats: [12]);

        await ShouldFailWithAsync(await CreateAsync(buyer, hold.HoldId, body: CreateBody(Guid.NewGuid(), hold.HoldId)), HttpStatusCode.NotFound, "event.not_found");
    }

    [Fact]
    public async Task A_second_post_for_the_same_hold_with_a_new_key_returns_the_same_order()
    {
        var buyer = await NewBuyerAsync();
        var hold = await HoldAsync(buyer, seats: [13]);

        var first = await JsonAsync(await CreateAsync(buyer, hold.HoldId), HttpStatusCode.Created);
        var second = await JsonAsync(await CreateAsync(buyer, hold.HoldId, body: CreateBody(_event, hold.HoldId, name: "Someone Else")));

        second.GetProperty("id").GetGuid().ShouldBe(first.GetProperty("id").GetGuid());
        second.GetProperty("buyer").GetProperty("name").GetString().ShouldBe("Nguyễn Văn An");
        (await OrderCountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task The_same_key_replays_the_stored_response()
    {
        var buyer = await NewBuyerAsync();
        var hold = await HoldAsync(buyer, seats: [14]);
        var key = Guid.NewGuid().ToString();

        var first = await CreateAsync(buyer, hold.HoldId, key);
        var firstText = await first.Content.ReadAsStringAsync(Ct);
        var retry = await CreateAsync(buyer, hold.HoldId, key);

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        retry.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await retry.Content.ReadAsStringAsync(Ct)).ShouldBe(firstText);
        (await OrderCountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Five_parallel_posts_for_one_hold_create_exactly_one_order()
    {
        var buyer = await NewBuyerAsync();
        var hold = await HoldAsync(buyer, seats: [15, 16], ga: 1);

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => CreateAsync(buyer, hold.HoldId)));

        var bodies = new List<JsonElement>();
        foreach (var response in responses)
        {
            response.StatusCode.ShouldBeOneOf(HttpStatusCode.Created, HttpStatusCode.OK);
            bodies.Add(JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone());
        }

        bodies.Select(b => b.GetProperty("id").GetGuid()).Distinct().Count().ShouldBe(1);
        responses.Count(r => r.StatusCode == HttpStatusCode.Created).ShouldBeLessThanOrEqualTo(1);
        (await OrderCountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Losing_the_race_at_the_unique_index_returns_the_winners_order()
    {
        var buyer = await NewBuyerAsync();
        var hold = await HoldAsync(buyer, ga: 2);
        Guid winner = Guid.Empty;
        var race = new RaceInterceptor(async () =>
        {
            // Another request got its order in after this one's lookup and before its insert.
            winner = (await SeedOrderForHoldAsync(buyer, hold.HoldId)).Id;
        });
        await using var racing = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.ConfigureDbContext<AppDbContext>(o => o.AddInterceptors(race))));
        using var client = racing.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = false });

        var response = await CreateAsync(buyer, hold.HoldId, client: client);

        race.Fired.ShouldBeTrue();
        (await JsonAsync(response)).GetProperty("id").GetGuid().ShouldBe(winner);
        (await OrderCountAsync()).ShouldBe(1);
    }

    private Task<Order> SeedOrderForHoldAsync(Buyer buyer, Guid holdId) => WithDbAsync(async db =>
    {
        var now = DateTimeOffset.UtcNow;
        var order = Order.Create(
            buyer.UserId, _event, holdId, [new OrderItem(_gaTier, null, new Money(800_000), 2)], new Money(1_600_000), new BuyerInfo("An", "an@example.com", null), now.AddMinutes(8), now);
        db.Orders.Add(order);
        await db.SaveChangesAsync(Ct);
        return order;
    });

    private sealed class RaceInterceptor(Func<Task> action) : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!Fired && eventData.Context!.ChangeTracker.Entries<Order>().Any(e => e.State == EntityState.Added))
            {
                Fired = true;
                await action();
            }

            return result;
        }
    }

    [Theory]
    [InlineData("", "an@example.com", "0901234567", "buyer.name")]
    [InlineData("   ", "an@example.com", "0901234567", "buyer.name")]
    [InlineData("An", "not-an-email", "0901234567", "buyer.email")]
    [InlineData("An", "an@example.com", "12345", "buyer.phone")]
    public async Task An_invalid_buyer_is_400_with_the_field(string name, string email, string phone, string field)
    {
        var buyer = await NewBuyerAsync();
        var hold = await HoldAsync(buyer, seats: [17]);

        var problem = await JsonAsync(await CreateAsync(buyer, hold.HoldId, body: CreateBody(_event, hold.HoldId, name, email, phone)), HttpStatusCode.BadRequest);

        problem.GetProperty("errors").TryGetProperty(field, out _).ShouldBeTrue(problem.ToString());
        (await OrderCountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task A_name_over_100_characters_is_400()
    {
        var buyer = await NewBuyerAsync();
        var hold = await HoldAsync(buyer, seats: [18]);

        await JsonAsync(await CreateAsync(buyer, hold.HoldId, body: CreateBody(_event, hold.HoldId, name: new string('a', 101))), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_missing_buyer_is_400_not_500()
    {
        var buyer = await NewBuyerAsync();
        var hold = await HoldAsync(buyer, seats: [19]);

        await JsonAsync(await CreateAsync(buyer, hold.HoldId, body: new { eventId = _event, holdId = hold.HoldId }), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Without_an_idempotency_key_or_a_login_the_post_is_refused()
    {
        var buyer = await NewBuyerAsync();
        var hold = await HoldAsync(buyer, seats: [20]);

        await ShouldFailWithAsync(await SendAsync(HttpMethod.Post, "/api/v1/orders", buyer.Access, CreateBody(_event, hold.HoldId)), HttpStatusCode.BadRequest, "idempotency.key_required");
        (await SendAsync(HttpMethod.Post, "/api/v1/orders", null, CreateBody(_event, hold.HoldId), Guid.NewGuid().ToString())).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_domain_rule_broken_by_the_hold_is_a_problem_not_a_500()
    {
        var buyer = await NewBuyerAsync();
        // The hold cap (8) and the phase limit are policy; the order's hard ceiling of 100 per line is a domain rule.
        var hold = await HoldAsync(buyer, ga: 101, maxTickets: 500);

        await ShouldFailWithAsync(await CreateAsync(buyer, hold.HoldId), HttpStatusCode.Conflict, "order.invalid_item");

        (await OrderCountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Redis_down_is_503_system_busy_and_no_order_is_made()
    {
        var buyer = await NewBuyerAsync();
        var hold = await HoldAsync(buyer, seats: [21]);
        await using var broken = Broken(out var switchboard);
        using var client = broken.CreateClient();
        switchboard.Down = true;

        await ShouldFailWithAsync(await CreateAsync(buyer, hold.HoldId, client: client), HttpStatusCode.ServiceUnavailable, "system.busy");

        (await OrderCountAsync()).ShouldBe(0);
    }

    // ---- GET /orders/{id} ----

    [Fact]
    public async Task The_owner_reads_the_order_other_buyers_and_strangers_do_not()
    {
        var owner = await NewBuyerAsync();
        var other = await NewBuyerAsync();
        var order = await CreateOrderAsync(owner, await HoldAsync(owner, seats: [22]));
        var id = order.GetProperty("id").GetGuid();

        var read = await JsonAsync(await GetOrderAsync(owner, id));

        read.GetProperty("id").GetGuid().ShouldBe(id);
        read.GetProperty("status").GetString().ShouldBe("Pending");
        read.GetProperty("total").GetInt64().ShouldBe(order.GetProperty("total").GetInt64());
        read.GetProperty("seats")[0].GetProperty("index").GetInt32().ShouldBe(22);
        read.GetProperty("expiresAt").GetDateTimeOffset().ShouldBe(order.GetProperty("expiresAt").GetDateTimeOffset());
        read.GetProperty("serverNow").GetDateTimeOffset().ShouldBeGreaterThanOrEqualTo(order.GetProperty("serverNow").GetDateTimeOffset());
        await ShouldFailWithAsync(await GetOrderAsync(other, id), HttpStatusCode.NotFound, "order.not_found");
        await ShouldFailWithAsync(await GetOrderAsync(owner, Guid.NewGuid()), HttpStatusCode.NotFound, "order.not_found");
        (await GetOrderAsync(null, id)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_paid_order_shows_paid_at_and_its_ticket_count()
    {
        var buyer = await NewBuyerAsync();
        var order = await CreateOrderAsync(buyer, await HoldAsync(buyer, seats: [23, 24]));
        var id = order.GetProperty("id").GetGuid();
        await SetStatusAsync(id, OrderStatus.Paid);
        await WithDbAsync(async db =>
        {
            var paid = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == id, Ct);
            db.Tickets.AddRange(paid.Items.Select(i => Ticket.Issue(id, _event, i.TierId, i.SeatIndex, buyer.UserId)));
            await db.SaveChangesAsync(Ct);
            return 0;
        });

        var read = await JsonAsync(await GetOrderAsync(buyer, id));

        read.GetProperty("status").GetString().ShouldBe("Paid");
        read.GetProperty("paidAt").GetDateTimeOffset().ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-1));
        read.GetProperty("ticketCount").GetInt32().ShouldBe(2);
        read.GetProperty("refundReason").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_refund_pending_order_shows_the_refund_reason()
    {
        var buyer = await NewBuyerAsync();
        var order = await CreateOrderAsync(buyer, await HoldAsync(buyer, seats: [25]));
        var id = order.GetProperty("id").GetGuid();
        await SetStatusAsync(id, OrderStatus.RefundPending);

        var read = await JsonAsync(await GetOrderAsync(buyer, id));

        read.GetProperty("status").GetString().ShouldBe("RefundPending");
        read.GetProperty("refundReason").GetString().ShouldBe("event_cancelled");
        read.GetProperty("ticketCount").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    // ---- POST /orders/{id}/cancel ----

    [Fact]
    public async Task Cancelling_releases_the_seats_and_cancels_the_order()
    {
        var buyer = await NewBuyerAsync();
        var hold = await HoldAsync(buyer, seats: [26, 27], ga: 3);
        var id = (await CreateOrderAsync(buyer, hold)).GetProperty("id").GetGuid();
        (await HeldSeatsAsync()).ShouldContain(26);

        var cancelled = await JsonAsync(await CancelAsync(buyer, id));

        cancelled.GetProperty("status").GetString().ShouldBe("Cancelled");
        (await HeldSeatsAsync()).ShouldNotContain(26);
        (await HeldSeatsAsync()).ShouldNotContain(27);
        (await Store.GetUserHoldAsync(_event, buyer.UserId, Ct)).ShouldBeNull();
        (await Store.GetSnapshotAsync(_event, _gaTiers, Ct)).GaRemaining[_gaTier].ShouldBe(2000);
        (await JsonAsync(await GetOrderAsync(buyer, id))).GetProperty("status").GetString().ShouldBe("Cancelled");
        // The seat is up for grabs again.
        var next = await NewBuyerAsync();
        (await HoldAsync(next, seats: [26])).Seats.ShouldBe([26]);
    }

    [Fact]
    public async Task Cancelling_twice_is_200_both_times_and_a_same_key_retry_replays()
    {
        var buyer = await NewBuyerAsync();
        var id = (await CreateOrderAsync(buyer, await HoldAsync(buyer, seats: [28]))).GetProperty("id").GetGuid();
        var key = Guid.NewGuid().ToString();

        var first = await CancelAsync(buyer, id, key);
        var replay = await CancelAsync(buyer, id, key);
        var again = await JsonAsync(await CancelAsync(buyer, id));

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await replay.Content.ReadAsStringAsync(Ct)).ShouldBe(await first.Content.ReadAsStringAsync(Ct));
        again.GetProperty("status").GetString().ShouldBe("Cancelled");
    }

    [Fact]
    public async Task Cancelling_when_the_hold_is_already_gone_still_cancels()
    {
        var buyer = await NewBuyerAsync();
        var hold = await HoldAsync(buyer, seats: [29]);
        var id = (await CreateOrderAsync(buyer, hold)).GetProperty("id").GetGuid();
        await Store.ReleaseHoldAsync(_event, hold.HoldId, _gaTiers, Ct);

        (await JsonAsync(await CancelAsync(buyer, id))).GetProperty("status").GetString().ShouldBe("Cancelled");
    }

    [Fact]
    public async Task Cancelling_leaves_a_newer_hold_of_the_buyer_alone()
    {
        var buyer = await NewBuyerAsync();
        var id = (await CreateOrderAsync(buyer, await HoldAsync(buyer, seats: [30]))).GetProperty("id").GetGuid();
        var newer = await HoldAsync(buyer, seats: [31], replace: true);

        (await JsonAsync(await CancelAsync(buyer, id))).GetProperty("status").GetString().ShouldBe("Cancelled");

        (await Store.GetUserHoldAsync(_event, buyer.UserId, Ct))!.HoldId.ShouldBe(newer.HoldId);
        (await HeldSeatsAsync()).ShouldContain(31);
    }

    [Fact]
    public async Task Cancelling_after_the_event_was_cancelled_still_works()
    {
        var buyer = await NewBuyerAsync();
        var id = (await CreateOrderAsync(buyer, await HoldAsync(buyer, seats: [32]))).GetProperty("id").GetGuid();
        await SetEventStatusAsync("Cancelled");
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<HybridCache>().RemoveByTagAsync(CachedEventCatalogReader.EventTag(_event), Ct);
        }

        (await JsonAsync(await CancelAsync(buyer, id))).GetProperty("status").GetString().ShouldBe("Cancelled");
    }

    [Theory]
    [InlineData(OrderStatus.AwaitingPayment)]
    [InlineData(OrderStatus.PaymentFailed)]
    [InlineData(OrderStatus.Paid)]
    [InlineData(OrderStatus.Expired)]
    [InlineData(OrderStatus.RefundPending)]
    public async Task An_order_past_pending_cannot_be_cancelled(OrderStatus status)
    {
        var buyer = await NewBuyerAsync();
        var id = (await CreateOrderAsync(buyer, await HoldAsync(buyer, seats: [33]))).GetProperty("id").GetGuid();
        await SetStatusAsync(id, status);

        await ShouldFailWithAsync(await CancelAsync(buyer, id), HttpStatusCode.Conflict, "order.not_payable");

        (await JsonAsync(await GetOrderAsync(buyer, id))).GetProperty("status").GetString().ShouldBe(status.ToString());
        (await HeldSeatsAsync()).ShouldContain(33);
    }

    [Fact]
    public async Task Another_buyer_cannot_cancel_and_the_hold_stays()
    {
        var owner = await NewBuyerAsync();
        var other = await NewBuyerAsync();
        var id = (await CreateOrderAsync(owner, await HoldAsync(owner, seats: [34]))).GetProperty("id").GetGuid();

        await ShouldFailWithAsync(await CancelAsync(other, id), HttpStatusCode.NotFound, "order.not_found");
        await ShouldFailWithAsync(await CancelAsync(owner, Guid.NewGuid()), HttpStatusCode.NotFound, "order.not_found");
        (await SendAsync(HttpMethod.Post, $"/api/v1/orders/{id}/cancel", null, key: Guid.NewGuid().ToString())).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        (await HeldSeatsAsync()).ShouldContain(34);
        (await JsonAsync(await GetOrderAsync(owner, id))).GetProperty("status").GetString().ShouldBe("Pending");
    }

    [Fact]
    public async Task A_cancel_that_loses_redis_still_cancelled_the_order_and_a_retry_frees_the_seats()
    {
        var buyer = await NewBuyerAsync();
        var id = (await CreateOrderAsync(buyer, await HoldAsync(buyer, seats: [35]))).GetProperty("id").GetGuid();
        await using var broken = Broken(out var switchboard);
        using var client = broken.CreateClient();
        switchboard.Down = true;

        await ShouldFailWithAsync(await CancelAsync(buyer, id, client: client), HttpStatusCode.ServiceUnavailable, "system.busy");

        (await JsonAsync(await GetOrderAsync(buyer, id))).GetProperty("status").GetString().ShouldBe("Cancelled");
        (await HeldSeatsAsync()).ShouldContain(35);
        switchboard.Down = false;
        (await JsonAsync(await CancelAsync(buyer, id, client: client))).GetProperty("status").GetString().ShouldBe("Cancelled");
        (await HeldSeatsAsync()).ShouldNotContain(35);
    }

    [Fact]
    public async Task A_cancelled_order_frees_the_hold_for_a_new_order()
    {
        var buyer = await NewBuyerAsync();
        var hold = await HoldAsync(buyer, seats: [36]);
        var first = (await CreateOrderAsync(buyer, hold)).GetProperty("id").GetGuid();
        await CancelAsync(buyer, first);
        var again = await HoldAsync(buyer, seats: [36]);

        var second = (await CreateOrderAsync(buyer, again)).GetProperty("id").GetGuid();

        second.ShouldNotBe(first);
    }

    // ---- GET /me/orders ----

    [Fact]
    public async Task My_orders_pages_newest_first_across_ties_and_only_shows_the_callers_orders()
    {
        var me = await NewBuyerAsync();
        var someoneElse = await NewBuyerAsync();
        var mine = await SeedOrdersAsync(me, 7);
        await SeedOrdersAsync(someoneElse, 3);
        // Rows 0-2 share one instant, 3-4 another, so the id tiebreak decides inside both groups and across page edges.
        var instants = new[] { 0, 0, 0, 1, 1, 2, 3 }.Select(i => new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero).AddMinutes(i)).ToArray();
        foreach (var (id, instant) in mine.Zip(instants))
        {
            await WithDbAsync(db => db.Database.ExecuteSqlRawAsync("UPDATE orders SET created_at = {0} WHERE id = {1}", [instant, id], Ct));
        }

        var expected = await WithDbAsync(db => db.Database
            .SqlQueryRaw<Guid>("SELECT id AS \"Value\" FROM orders WHERE user_id = {0} ORDER BY created_at DESC, id DESC", me.UserId).ToListAsync(Ct));
        var seen = new List<Guid>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var path = "/api/v1/me/orders?limit=3" + (cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}");
            var page = await JsonAsync(await SendAsync(HttpMethod.Get, path, me.Access));
            seen.AddRange(page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
            cursor = page.GetProperty("nextCursor").GetString();
            pages++;
            page.GetProperty("items").GetArrayLength().ShouldBeLessThanOrEqualTo(3);
        }
        while (cursor is not null && pages < 10);

        pages.ShouldBe(3);
        seen.ShouldBe(expected);
        seen.Distinct().Count().ShouldBe(7);
    }

    [Fact]
    public async Task My_orders_items_carry_event_details_totals_and_status()
    {
        var buyer = await NewBuyerAsync();
        var order = await CreateOrderAsync(buyer, await HoldAsync(buyer, seats: [37, 38], ga: 2));
        var id = order.GetProperty("id").GetGuid();

        var list = await JsonAsync(await SendAsync(HttpMethod.Get, "/api/v1/me/orders", buyer.Access));

        var item = list.GetProperty("items").EnumerateArray().Single();
        item.GetProperty("id").GetGuid().ShouldBe(id);
        item.GetProperty("eventId").GetGuid().ShouldBe(_event);
        item.GetProperty("eventTitle").GetString().ShouldBe("Sơn Tùng M-TP Live");
        item.GetProperty("eventSlug").GetString().ShouldBe("son-tung-mtp-live");
        item.GetProperty("eventStartsAt").GetDateTimeOffset().ShouldBeGreaterThan(DateTimeOffset.UtcNow);
        item.GetProperty("status").GetString().ShouldBe("Pending");
        item.GetProperty("total").GetInt64().ShouldBe(order.GetProperty("total").GetInt64());
        item.GetProperty("itemCount").GetInt32().ShouldBe(4);
        // Postgres keeps microseconds, the created order still carries .NET ticks.
        item.GetProperty("createdAt").GetDateTimeOffset().ShouldBe(order.GetProperty("createdAt").GetDateTimeOffset(), TimeSpan.FromMilliseconds(1));
        item.GetProperty("paidAt").ValueKind.ShouldBe(JsonValueKind.Null);
        list.GetProperty("nextCursor").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task My_orders_is_empty_for_a_new_buyer_and_refuses_bad_paging_and_strangers()
    {
        var buyer = await NewBuyerAsync();

        (await JsonAsync(await SendAsync(HttpMethod.Get, "/api/v1/me/orders", buyer.Access))).GetProperty("items").GetArrayLength().ShouldBe(0);
        await JsonAsync(await SendAsync(HttpMethod.Get, "/api/v1/me/orders?limit=51", buyer.Access), HttpStatusCode.BadRequest);
        await JsonAsync(await SendAsync(HttpMethod.Get, "/api/v1/me/orders?limit=0", buyer.Access), HttpStatusCode.BadRequest);
        await JsonAsync(await SendAsync(HttpMethod.Get, "/api/v1/me/orders?cursor=garbage!", buyer.Access), HttpStatusCode.BadRequest);
        (await SendAsync(HttpMethod.Get, "/api/v1/me/orders", null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task My_orders_defaults_to_20_and_allows_up_to_50()
    {
        var buyer = await NewBuyerAsync();
        await SeedOrdersAsync(buyer, 22);

        var defaults = await JsonAsync(await SendAsync(HttpMethod.Get, "/api/v1/me/orders", buyer.Access));
        var max = await JsonAsync(await SendAsync(HttpMethod.Get, "/api/v1/me/orders?limit=50", buyer.Access));

        defaults.GetProperty("items").GetArrayLength().ShouldBe(20);
        defaults.GetProperty("nextCursor").GetString().ShouldNotBeNull();
        max.GetProperty("items").GetArrayLength().ShouldBe(22);
        max.GetProperty("nextCursor").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    /// <summary>Inserts orders straight into the database, one distinct hold id each.</summary>
    private Task<List<Guid>> SeedOrdersAsync(Buyer buyer, int count) => WithDbAsync(async db =>
    {
        var now = DateTimeOffset.UtcNow;
        var orders = Enumerable.Range(0, count).Select(i => Order.Create(
            buyer.UserId,
            _event,
            Guid.NewGuid(),
            [new OrderItem(_gaTier, null, new Money(800_000), 2), new OrderItem(_gaTier, null, new Money(800_000), 1)],
            new Money(2_400_000),
            new BuyerInfo("An", "an@example.com", null),
            now.AddMinutes(8),
            now)).ToList();
        db.Orders.AddRange(orders);
        await db.SaveChangesAsync(Ct);
        return orders.Select(o => o.Id).ToList();
    });

    private async Task<int[]> HeldSeatsAsync() => [.. (await Store.GetSnapshotAsync(_event, _gaTiers, Ct)).Held];

    // ---- Redis switch ----

    /// <summary>Wraps the real inventory so a test can make the hold lookups and releases fail like a Redis outage.</summary>
    private WebApplicationFactoryHandle Broken(out Switchboard switchboard)
    {
        var board = new Switchboard();
        var proxy = DispatchProxy.Create<IInventoryStore, SwitchableStore>();
        ((SwitchableStore)(object)proxy).Setup(Store, board);
        switchboard = board;
        return new WebApplicationFactoryHandle(_factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton(proxy))));
    }

    public sealed class Switchboard
    {
        public bool Down { get; set; }
    }

    private sealed class WebApplicationFactoryHandle(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory) : IAsyncDisposable
    {
        public HttpClient CreateClient() => factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = false });

        public ValueTask DisposeAsync() => factory.DisposeAsync();
    }

    public class SwitchableStore : DispatchProxy
    {
        private IInventoryStore _inner = null!;
        private Switchboard _board = null!;

        internal void Setup(IInventoryStore inner, Switchboard board) => (_inner, _board) = (inner, board);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (_board.Down && targetMethod!.Name is nameof(IInventoryStore.GetUserHoldAsync) or nameof(IInventoryStore.ReleaseHoldAsync) or nameof(IInventoryStore.HoldRecordedAsync))
            {
                throw new InventoryUnavailableException("down");
            }

            try
            {
                return targetMethod!.Invoke(_inner, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                throw ex.InnerException;
            }
        }
    }
}
