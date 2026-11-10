using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Application.Abstractions.Inventory;
using TicketRush.Application.Abstractions.Messaging;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Application.Abstractions.Queue;
using TicketRush.Domain.Events;
using TicketRush.Domain.Orders;
using TicketRush.Domain.Primitives;
using TicketRush.Infrastructure.Inventory;
using TicketRush.Infrastructure.Outbox;
using TicketRush.Infrastructure.Payments;
using TicketRush.Infrastructure.Persistence;
using TicketRush.Infrastructure.Persistence.Seed;
using TicketRush.Infrastructure.Queue;
using TicketRush.Infrastructure.Redis;
using Order = TicketRush.Domain.Orders.Order;

namespace TicketRush.Api.IntegrationTests.Orders;

/// <summary>
/// The consumers of <see cref="OrderPaid"/> (spec 08): the inventory commit (07) and leaving the waiting room (06), both
/// idempotent under the outbox's at-least-once delivery, each with its own consumer receipt.
/// </summary>
public sealed class OrderPaidConsumersTests(ContainersFixture containers) : IAsyncLifetime
{
    private const string Secret = "test-mock-webhook-secret-0123456789";
    private const string WebhookPath = "/api/v1/webhooks/payments/mock";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ApiFactory _factory = null!;
    private AsyncServiceScope _scope;
    private Event _event = null!;
    private TicketTier _ga = null!;
    private TicketTier _seated = null!;
    private List<int> _seats = [];
    private IReadOnlyList<Guid> _gaTiers = [];
    private RedisKeys _keys = null!;
    private IDatabase _redis = null!;
    private IInventoryStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = new ApiFactory(await containers.CreateDatabaseAsync(), containers.RedisConnectionString);
        _scope = _factory.Services.CreateAsyncScope();
        var services = _scope.ServiceProvider;
        await services.GetRequiredService<DevDataSeeder>().SeedAsync(Ct);
        var db = services.GetRequiredService<AppDbContext>();
        _event = await db.Events.AsNoTracking().Include(e => e.Phases).Include(e => e.Tiers).SingleAsync(e => e.Slug == "son-tung-mtp-live", Ct);
        _ga = _event.Tiers.First(t => t.Kind == TicketKind.GeneralAdmission);
        _seated = _event.Tiers.First(t => t.Kind == TicketKind.Seated);
        _gaTiers = [.. _event.Tiers.Where(t => t.Kind == TicketKind.GeneralAdmission).Select(t => t.Id)];
        _seats = await db.EventSeats.AsNoTracking().Where(s => s.EventId == _event.Id && s.TierId == _seated.Id)
            .OrderBy(s => s.Index).Select(s => s.Index).Take(20).ToListAsync(Ct);
        _keys = services.GetRequiredService<RedisKeys>();
        _redis = services.GetRequiredService<IConnectionMultiplexer>().GetDatabase();
        _store = services.GetRequiredService<IInventoryStore>();
        await services.GetRequiredService<InventorySeeder>().SeedAsync(_event.Id, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _scope.DisposeAsync();
        await _factory.DisposeAsync();
    }

    // ---------------------------------------------------------------- fixtures

    private static IntegrationEventContext Context => new(Guid.NewGuid(), DateTimeOffset.UtcNow, 1);

    private async Task<Guid> HoldAsync(Guid user, int[] seats, params GaLine[] ga)
    {
        var request = new HoldRequest(_event.Id, user, seats, ga, _gaTiers, 8, false, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(8));
        var outcome = await _store.CreateHoldAsync(request, Ct);
        outcome.Status.ShouldBe(HoldStatus.Ok);
        return outcome.HoldId!.Value;
    }

    private OrderPaid Paid(Guid user, Guid holdId, int[] seats, int ga = 0)
    {
        var lines = seats.Select(seat => new OrderPaidLine(_seated.Id, seat, 1)).ToList();
        if (ga > 0)
        {
            lines.Add(new OrderPaidLine(_ga.Id, null, ga));
        }

        return new OrderPaid(Guid.NewGuid(), user, _event.Id, holdId, 1_000_000, "VND", seats.Length + ga, DateTimeOffset.UtcNow, lines);
    }

    private Task CommitAsync(OrderPaid paid) =>
        _scope.ServiceProvider.GetRequiredService<OrderPaidInventoryCommitHandler>().HandleAsync(paid, Context, Ct);

    private Task LeaveAsync(OrderPaid paid) =>
        _scope.ServiceProvider.GetRequiredService<OrderPaidLeaveQueueHandler>().HandleAsync(paid, Context, Ct);

    private Task<InventorySnapshot> SnapshotAsync() => _store.GetSnapshotAsync(_event.Id, _gaTiers, Ct);

    private async Task<int> BoughtAsync(Guid user) => (int)await _redis.StringGetAsync(_keys.InvBought(_event.Id, user));

    private async Task<int> GaHeldAsync() => (int)await _redis.HashGetAsync(_keys.InvGaHeld(_event.Id), _ga.Id.ToString("N"));

    private async Task<ConcurrentQueue<string>> ListenAsync()
    {
        var messages = new ConcurrentQueue<string>();
        await _scope.ServiceProvider.GetRequiredService<IConnectionMultiplexer>().GetSubscriber()
            .SubscribeAsync(_keys.InvChanges(_event.Id), (_, message) => messages.Enqueue(message!));
        return messages;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, Ct);
        }

        condition().ShouldBeTrue();
    }

    // ================================================================ inventory commit

    [Fact]
    public async Task A_paid_seated_order_marks_its_seats_sold_counts_them_as_bought_and_publishes_a_seat_delta()
    {
        var user = Guid.NewGuid();
        int[] seats = [_seats[3], _seats[1]];
        var hold = await HoldAsync(user, seats);
        var messages = await ListenAsync();

        await CommitAsync(Paid(user, hold, seats));

        var snapshot = await SnapshotAsync();
        snapshot.Sold.ShouldBe([_seats[1], _seats[3]]);
        snapshot.Held.ShouldBeEmpty();
        (await BoughtAsync(user)).ShouldBe(2);
        (await _store.GetUserHoldAsync(_event.Id, user, Ct)).ShouldBeNull();
        await WaitUntilAsync(() => messages.Any(m => m.Contains($"\"sold\":[{_seats[1]},{_seats[3]}]", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_paid_general_admission_order_takes_its_tickets_exactly_once_and_releases_the_hold_count()
    {
        var user = Guid.NewGuid();
        var hold = await HoldAsync(user, [], new GaLine(_ga.Id, 3));
        var afterHold = await SnapshotAsync();
        (await GaHeldAsync()).ShouldBe(3);
        var paid = Paid(user, hold, [], ga: 3);

        await CommitAsync(paid);
        await CommitAsync(paid);

        var snapshot = await SnapshotAsync();
        snapshot.GaRemaining[_ga.Id].ShouldBe(afterHold.GaRemaining[_ga.Id], "the live hold had already taken the tickets");
        (await GaHeldAsync()).ShouldBe(0);
        (await BoughtAsync(user)).ShouldBe(3);
    }

    [Fact]
    public async Task Redelivering_the_same_order_paid_changes_nothing_and_publishes_nothing()
    {
        var user = Guid.NewGuid();
        int[] seats = [_seats[0], _seats[1]];
        var hold = await HoldAsync(user, seats, new GaLine(_ga.Id, 1));
        var paid = Paid(user, hold, seats, ga: 1);
        await CommitAsync(paid);
        var before = await SnapshotAsync();
        var messages = await ListenAsync();

        await CommitAsync(paid);

        var after = await SnapshotAsync();
        after.Version.ShouldBe(before.Version);
        after.Sold.ShouldBe(before.Sold);
        after.GaRemaining[_ga.Id].ShouldBe(before.GaRemaining[_ga.Id]);
        (await BoughtAsync(user)).ShouldBe(3);
        (await _store.GetCommitFenceAsync(_event.Id, Ct)).ShouldBe(1);
        await Task.Delay(100, Ct);
        messages.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_late_payment_after_the_hold_expired_still_sells_the_seats_and_takes_the_general_admission_tickets()
    {
        var user = Guid.NewGuid();
        var hold = await HoldAsync(user, [_seats[4]], new GaLine(_ga.Id, 2));
        await _store.ReleaseHoldAsync(_event.Id, hold, _gaTiers, Ct);
        var afterExpiry = await SnapshotAsync();
        var paid = Paid(user, hold, [_seats[4]], ga: 2);

        await CommitAsync(paid);
        await CommitAsync(paid);

        var snapshot = await SnapshotAsync();
        snapshot.Sold.ShouldBe([_seats[4]]);
        snapshot.GaRemaining[_ga.Id].ShouldBe(afterExpiry.GaRemaining[_ga.Id] - 2);
        (await BoughtAsync(user)).ShouldBe(3);
    }

    [Fact]
    public async Task A_late_payment_for_a_seat_someone_else_holds_now_sells_it_and_drops_the_other_hold_on_it()
    {
        var late = Guid.NewGuid();
        var other = Guid.NewGuid();
        var expired = await HoldAsync(late, [_seats[5]]);
        await _store.ReleaseHoldAsync(_event.Id, expired, _gaTiers, Ct);
        await HoldAsync(other, [_seats[5]]);

        await CommitAsync(Paid(late, expired, [_seats[5]]));

        var snapshot = await SnapshotAsync();
        snapshot.Sold.ShouldBe([_seats[5]]);
        snapshot.Held.ShouldBeEmpty("a sold seat must not stay held by the other buyer");
    }

    [Fact]
    public async Task While_redis_is_down_the_commit_throws_so_the_outbox_retries_and_a_later_delivery_succeeds()
    {
        var user = Guid.NewGuid();
        var hold = await HoldAsync(user, [_seats[6]]);
        var paid = Paid(user, hold, [_seats[6]]);
        await using var offline = await ConnectionMultiplexer.ConnectAsync("127.0.0.1:1,abortConnect=false,connectTimeout=300,syncTimeout=500,asyncTimeout=500");
        var down = new RedisInventoryStore(offline, _keys, TimeProvider.System);
        var broken = new OrderPaidInventoryCommitHandler(_scope.ServiceProvider.GetRequiredService<IAppDbContext>(), down, NullLogger<OrderPaidInventoryCommitHandler>.Instance);

        await Should.ThrowAsync<InventoryUnavailableException>(() => broken.HandleAsync(paid, Context, Ct));
        (await SnapshotAsync()).Sold.ShouldBeEmpty();

        await CommitAsync(paid);

        (await SnapshotAsync()).Sold.ShouldBe([_seats[6]]);
    }

    // ================================================================ leave the waiting room

    private async Task AdmitAsync(Guid user)
    {
        foreach (var phase in _event.Phases)
        {
            await _redis.SortedSetAddAsync(_keys.QueueActive(phase.Id), user.ToString(), DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeMilliseconds());
            await _redis.HashSetAsync(_keys.QueueTokens(phase.Id), user.ToString(), "token-1");
            await _redis.HashSetAsync(_keys.QueueLine(phase.Id), user.ToString(), 7);
        }
    }

    private async Task<bool> InAnyRoomAsync(Guid user)
    {
        var room = _scope.ServiceProvider.GetRequiredService<IWaitingRoom>();
        foreach (var phase in _event.Phases)
        {
            var window = new SaleWindow(_event.Id, phase.Id, phase.OpensAt, phase.ClosesAt, phase.QueueEnabled);
            if (await room.IsActiveAsync(phase.Id, user, Ct)
                || await room.IsCurrentTokenAsync(phase.Id, user, "token-1", Ct)
                || await room.IsMemberAsync(window, user, Ct))
            {
                return true;
            }
        }

        return false;
    }

    [Fact]
    public async Task Paying_removes_the_buyer_from_the_waiting_room_of_every_phase_and_running_it_again_is_harmless()
    {
        var buyer = Guid.NewGuid();
        var bystander = Guid.NewGuid();
        await AdmitAsync(buyer);
        await AdmitAsync(bystander);
        var paid = Paid(buyer, Guid.NewGuid(), [_seats[7]]);
        (await InAnyRoomAsync(buyer)).ShouldBeTrue();

        await LeaveAsync(paid);
        await LeaveAsync(paid);

        (await InAnyRoomAsync(buyer)).ShouldBeFalse();
        (await InAnyRoomAsync(bystander)).ShouldBeTrue("someone else's place in the line is not touched");
    }

    [Fact]
    public async Task A_registration_for_a_phase_that_opens_after_the_payment_survives()
    {
        var buyer = Guid.NewGuid();
        var phase = _event.Phases[0];
        var window = new SaleWindow(_event.Id, phase.Id, phase.OpensAt, phase.ClosesAt, phase.QueueEnabled);
        await _redis.SetAddAsync(_keys.QueuePre(phase.Id), buyer.ToString());
        var room = _scope.ServiceProvider.GetRequiredService<IWaitingRoom>();
        (await room.IsMemberAsync(window, buyer, Ct)).ShouldBeTrue();

        await LeaveAsync(Paid(buyer, Guid.NewGuid(), [_seats[7]]) with { PaidAt = phase.OpensAt.AddMinutes(-1) });

        (await room.IsMemberAsync(window, buyer, Ct)).ShouldBeTrue("the phase had not opened when the buyer paid, so its pre-queue place is theirs to keep");
        await LeaveAsync(Paid(buyer, Guid.NewGuid(), [_seats[7]]) with { PaidAt = phase.OpensAt });
        (await room.IsMemberAsync(window, buyer, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_buyer_who_was_never_in_the_waiting_room_is_not_an_error()
    {
        var paid = Paid(Guid.NewGuid(), Guid.NewGuid(), [_seats[8]]);

        await Should.NotThrowAsync(() => LeaveAsync(paid));
    }

    // ================================================================ registration and end to end

    [Fact]
    public void The_dispatcher_delivers_order_paid_to_both_consumers_as_separate_handlers()
    {
        var handlers = _factory.Services.GetServices<IntegrationEventHandlerRegistration>()
            .Where(r => r.EventType == typeof(OrderPaid))
            .Select(r => r.HandlerType)
            .ToList();

        // The outbox runs handlers in this order and stops at the first failure: the commit must not wait behind the leave.
        handlers.ShouldBe([typeof(OrderPaidInventoryCommitHandler), typeof(OrderPaidLeaveQueueHandler)]);
        _factory.Services.GetRequiredService<DomainEventTypeRegistry>().TryResolve(nameof(OrderPaid), out _).ShouldBeTrue();
    }

    /// <summary>
    /// The seeded events still have their publish messages pending; delivering them re-reads the database, so they must
    /// not be delivered between the payment and the commit being tested (a freshly published event has none pending).
    /// </summary>
    private Task SettleSeededOutboxAsync() => _factory.DrainOutboxAsync(Ct);

    private async Task<(Guid OrderId, string Session, long Total)> PlaceExpiredOrderAsync(Guid user, int seat, int ga)
    {
        var db = _scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;
        OrderItem[] items = [new(_seated.Id, seat, _seated.Price, 1), new(_ga.Id, null, _ga.Price, ga)];
        var total = items.Aggregate(Money.Zero(), (sum, item) => sum + item.LineTotal());
        var order = Order.Create(user, _event.Id, Guid.CreateVersion7(), items, total, new BuyerInfo("Nguyễn Văn An", "an@example.com", null), now.AddMinutes(8), now);
        order.StartPayment(now);
        var session = $"sess_{Guid.NewGuid():N}";
        db.PaymentAttempts.Add(PaymentAttempt.Create(order.Id, "mock", session, $"http://localhost:3000/mock-gateway/{session}", order.Total, order.ExpiresAt, now));
        order.Expire();
        db.Orders.Add(order);
        await db.SaveChangesAsync(Ct);
        return (order.Id, session, order.Total.Amount);
    }

    private static async Task<string> PayByWebhookAsync(HttpClient client, string session, long total)
    {
        var body = new MockWebhookPayload($"evt_mock_{Guid.NewGuid():N}", MockWebhookPayload.SucceededType, session, total, "VND", DateTimeOffset.UtcNow).ToJson();
        var request = new HttpRequestMessage(HttpMethod.Post, WebhookPath) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.TryAddWithoutValidation("X-Signature", WebhookSignature.Sign(Secret, body, DateTimeOffset.UtcNow));
        var response = await client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, text);
        return JsonDocument.Parse(text).RootElement.GetProperty("outcome").GetString()!;
    }

    private Task<string[]> ReceiptsAsync(Guid orderId) => _scope.ServiceProvider.GetRequiredService<AppDbContext>().ConsumerReceipts.AsNoTracking()
        .Join(_scope.ServiceProvider.GetRequiredService<AppDbContext>().OutboxMessages.Where(m => m.AggregateId == orderId && m.Type == nameof(OrderPaid)),
            r => r.MessageId, m => m.Id, (r, _) => r.Handler)
        .ToArrayAsync(Ct);

    [Fact]
    public async Task A_late_success_with_the_seat_still_free_is_paid_and_the_outbox_marks_the_seat_sold_and_empties_the_queue()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        await SettleSeededOutboxAsync();
        var user = Guid.NewGuid();
        await AdmitAsync(user);
        var gaBefore = (await SnapshotAsync()).GaRemaining[_ga.Id];
        var (orderId, session, total) = await PlaceExpiredOrderAsync(user, _seats[9], ga: 2);

        (await PayByWebhookAsync(client, session, total)).ShouldBe("paid");
        await _factory.DrainOutboxAsync(Ct);

        var snapshot = await SnapshotAsync();
        snapshot.Sold.ShouldBe([_seats[9]]);
        snapshot.GaRemaining[_ga.Id].ShouldBe(gaBefore - 2);
        (await BoughtAsync(user)).ShouldBe(3);
        (await InAnyRoomAsync(user)).ShouldBeFalse();
        (await ReceiptsAsync(orderId)).ShouldBe([typeof(OrderPaidInventoryCommitHandler).FullName!, typeof(OrderPaidLeaveQueueHandler).FullName!], ignoreOrder: true);
    }

    [Fact]
    public async Task A_reconcile_between_the_payment_and_the_commit_does_not_leave_the_bought_count_inflated()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        await SettleSeededOutboxAsync();
        var seeder = _scope.ServiceProvider.GetRequiredService<InventorySeeder>();
        var user = Guid.NewGuid();
        var gaBefore = (await SnapshotAsync()).GaRemaining[_ga.Id];
        var (_, session, total) = await PlaceExpiredOrderAsync(user, _seats[11], ga: 2);
        (await PayByWebhookAsync(client, session, total)).ShouldBe("paid");

        await seeder.SeedAsync(_event.Id, Ct); // the database already has the order, Redis does not yet
        await _factory.DrainOutboxAsync(Ct); // the commit now counts the order a second time
        await seeder.SeedAsync(_event.Id, Ct); // the next reconcile repairs it

        (await BoughtAsync(user)).ShouldBe(3);
        (await SnapshotAsync()).GaRemaining[_ga.Id].ShouldBe(gaBefore - 2);
        (await SnapshotAsync()).Sold.ShouldBe([_seats[11]]);
    }

    [Fact]
    public async Task A_waiting_room_failure_does_not_block_the_inventory_commit_and_the_leave_is_retried()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        await SettleSeededOutboxAsync();
        var user = Guid.NewGuid();
        var (orderId, session, total) = await PlaceExpiredOrderAsync(user, _seats[10], ga: 1);
        (await PayByWebhookAsync(client, session, total)).ShouldBe("paid");
        await using var brokenRoom = _factory.WithWebHostBuilder(host => host.ConfigureTestServices(services => services.AddSingleton<IWaitingRoom, UnavailableRoom>()));
        var processor = brokenRoom.Services.GetRequiredService<OutboxProcessor>();

        while (await processor.ProcessBatchAsync(Ct) > 0)
        {
        }

        (await SnapshotAsync()).Sold.ShouldBe([_seats[10]]);
        (await ReceiptsAsync(orderId)).ShouldBe([typeof(OrderPaidInventoryCommitHandler).FullName!]);
        var message = await _scope.ServiceProvider.GetRequiredService<AppDbContext>().OutboxMessages.AsNoTracking()
            .SingleAsync(m => m.AggregateId == orderId && m.Type == nameof(OrderPaid), Ct);
        message.ProcessedAt.ShouldBeNull("the message stays pending until the leave succeeds");
        message.Attempts.ShouldBe(1);
    }

    private sealed class UnavailableRoom : IWaitingRoom
    {
        public Task<JoinResult> JoinAsync(SaleWindow sale, Guid userId, CancellationToken ct) => throw Down();

        public Task<ClaimResult> StatusAsync(SaleWindow sale, Guid userId, CancellationToken ct) => throw Down();

        public Task<bool> IsMemberAsync(SaleWindow sale, Guid userId, CancellationToken ct) => throw Down();

        public Task LeaveAsync(SaleWindow sale, Guid userId, CancellationToken ct) => throw Down();

        public Task<bool> IsActiveAsync(Guid phaseId, Guid userId, CancellationToken ct) => throw Down();

        public Task<bool> IsCurrentTokenAsync(Guid phaseId, Guid userId, string token, CancellationToken ct) => throw Down();

        public Task<double> GetRateEmaAsync(Guid phaseId, CancellationToken ct) => throw Down();

        private static QueueUnavailableException Down() => new("down");
    }
}
