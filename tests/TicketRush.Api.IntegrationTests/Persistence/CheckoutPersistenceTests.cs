using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Domain.Events;
using TicketRush.Domain.Orders;
using TicketRush.Domain.Primitives;
using TicketRush.Domain.Tickets;
using TicketRush.Infrastructure;
using TicketRush.Infrastructure.Inventory;
using TicketRush.Infrastructure.Persistence;

namespace TicketRush.Api.IntegrationTests.Persistence;

/// <summary>Spec 08 persistence: the database guards (C1), xmin on orders, encrypted ticket secrets and the outbox.</summary>
public sealed class CheckoutPersistenceTests(ContainersFixture containers) : IAsyncLifetime
{
    private const string UniqueViolation = "23505";

    private IHost _host = null!;
    private Guid _eventId;
    private Guid _seatedTier;
    private List<Guid> _gaTiers = [];

    public async ValueTask InitializeAsync()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(TestSettings.For(await containers.CreateDatabaseAsync(), containers.RedisConnectionString));
        builder.AddPersistence();
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        _host = builder.Build();

        await using var scope = Scope();
        await SeedEventAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public ValueTask DisposeAsync()
    {
        _host.Dispose();
        return ValueTask.CompletedTask;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AsyncServiceScope Scope() => _host.Services.CreateAsyncScope();

    private static AppDbContext Db(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<AppDbContext>();

    private async Task SeedEventAsync(AppDbContext db)
    {
        var venue = Domain.Venues.Venue.Create("Venue", "Address", "Hà Nội", null, 100);
        var now = DateTimeOffset.UtcNow;
        var evt = Event.CreateDraft(Guid.CreateVersion7(), "checkout", "Checkout", "concert", venue, now.AddDays(10), now.AddDays(10).AddHours(2));
        var tiers = new List<TierDraft> { new(null, "Ngồi", new Money(800_000), TicketKind.Seated, null, "#7c3aed") };
        tiers.AddRange(Enumerable.Range(1, 6).Select(i => new TierDraft(null, $"Đứng {i}", new Money(300_000), TicketKind.GeneralAdmission, 3, "#0f766e")));
        evt.ReplaceTiers(tiers);
        db.Venues.Add(venue);
        db.Events.Add(evt);
        await db.SaveChangesAsync(Ct);
        _eventId = evt.Id;
        _seatedTier = evt.Tiers.Single(t => t.Kind == TicketKind.Seated).Id;
        _gaTiers = evt.Tiers.Where(t => t.Kind == TicketKind.GeneralAdmission).Select(t => t.Id).ToList();
    }

    private Order NewOrder(Guid? holdId = null, Guid? userId = null, params int[] seats)
    {
        var lines = (seats.Length == 0 ? [12, 13] : seats).Select(seat => new OrderItem(_seatedTier, seat, new Money(800_000), 1)).ToList();
        var now = DateTimeOffset.UtcNow;
        return Order.Create(
            userId ?? Guid.CreateVersion7(),
            _eventId,
            holdId ?? Guid.CreateVersion7(),
            lines,
            new Money(800_000 * lines.Count),
            new BuyerInfo("Nguyễn Văn A", "a@example.com", "0901234567"),
            now.AddMinutes(10),
            now);
    }

    private async Task<Order> SaveOrderAsync(Order order)
    {
        await using var scope = Scope();
        var db = Db(scope);
        db.Orders.Add(order);
        await db.SaveChangesAsync(Ct);
        return order;
    }

    private async Task<Ticket> SaveTicketAsync(Order order, int? seat, Guid? tier = null, Action<Ticket>? change = null)
    {
        await using var scope = Scope();
        var db = Db(scope);
        var ticket = Ticket.Issue(order.Id, _eventId, tier ?? (seat is null ? _gaTiers[0] : _seatedTier), seat, order.UserId);
        change?.Invoke(ticket);
        db.Tickets.Add(ticket);
        await db.SaveChangesAsync(Ct);
        return ticket;
    }

    private static async Task<PostgresException> ViolationAsync(Func<Task> act)
    {
        var exception = await Should.ThrowAsync<DbUpdateException>(act);
        var postgres = exception.InnerException.ShouldBeOfType<PostgresException>();
        postgres.SqlState.ShouldBe(UniqueViolation);
        return postgres;
    }

    private async Task<int> SoldCountAsync(Guid tierId)
    {
        await using var scope = Scope();
        return await Db(scope).Database.SqlQuery<int>($"SELECT sold_count AS \"Value\" FROM ticket_tiers WHERE id = {tierId}").SingleAsync(Ct);
    }

    [Fact]
    public async Task The_migrated_schema_matches_the_model()
    {
        await using var scope = Scope();
        Db(scope).Database.HasPendingModelChanges().ShouldBeFalse();
        (await Db(scope).Database.GetPendingMigrationsAsync(Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_order_round_trips_with_items_buyer_money_and_status()
    {
        var order = await SaveOrderAsync(NewOrder());

        await using var scope = Scope();
        var loaded = await Db(scope).Orders.AsNoTracking().SingleAsync(o => o.Id == order.Id, Ct);

        loaded.Items.ShouldBe(order.Items);
        loaded.Items.Count.ShouldBe(2);
        loaded.Total.ShouldBe(new Money(1_600_000));
        loaded.Buyer.ShouldBe(new BuyerInfo("Nguyễn Văn A", "a@example.com", "0901234567"));
        loaded.Status.ShouldBe(OrderStatus.Pending);
        loaded.UserId.ShouldBe(order.UserId);
        loaded.HoldId.ShouldBe(order.HoldId);
        loaded.ExpiresAt.ShouldBe(order.ExpiresAt, TimeSpan.FromMilliseconds(1));
        loaded.CreatedAt.ShouldBeGreaterThan(DateTimeOffset.MinValue);
        loaded.PaidAt.ShouldBeNull();
    }

    [Fact]
    public async Task A_general_admission_line_and_a_missing_phone_round_trip()
    {
        var now = DateTimeOffset.UtcNow;
        var order = Order.Create(
            Guid.CreateVersion7(), _eventId, Guid.CreateVersion7(), [new OrderItem(_gaTiers[0], null, new Money(300_000), 3)],
            new Money(900_000), new BuyerInfo("B", "b@example.com", null), now.AddMinutes(10), now);
        await SaveOrderAsync(order);

        await using var scope = Scope();
        var loaded = await Db(scope).Orders.AsNoTracking().SingleAsync(o => o.Id == order.Id, Ct);

        loaded.Items.ShouldHaveSingleItem().ShouldBe(new OrderItem(_gaTiers[0], null, new Money(300_000), 3));
        loaded.Buyer.Phone.ShouldBeNull();
    }

    [Fact]
    public async Task Order_state_changes_are_saved_and_their_events_reach_the_outbox_in_the_same_save()
    {
        var order = await SaveOrderAsync(NewOrder());

        await using (var scope = Scope())
        {
            var db = Db(scope);
            var tracked = await db.Orders.SingleAsync(o => o.Id == order.Id, Ct);
            tracked.StartPayment(DateTimeOffset.UtcNow);
            tracked.MarkPaid(DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(Ct);
            tracked.DomainEvents.ShouldBeEmpty();
        }

        await using var check = Scope();
        var loaded = await Db(check).Orders.AsNoTracking().SingleAsync(o => o.Id == order.Id, Ct);
        loaded.Status.ShouldBe(OrderStatus.Paid);
        loaded.PaidAt.ShouldNotBeNull();
        var message = await Db(check).OutboxMessages.AsNoTracking().SingleAsync(Ct);
        message.Type.ShouldBe(nameof(OrderPaid));
        message.AggregateId.ShouldBe(order.Id);
        message.Payload.ShouldContain(order.HoldId.ToString());
    }

    [Fact]
    public async Task Order_status_is_stored_as_a_string()
    {
        var order = await SaveOrderAsync(NewOrder());

        await using var scope = Scope();
        var status = await Db(scope).Database.SqlQuery<string>($"SELECT status AS \"Value\" FROM orders WHERE id = {order.Id}").SingleAsync(Ct);

        status.ShouldBe("Pending");
    }

    [Fact]
    public async Task A_stale_order_save_raises_a_concurrency_conflict()
    {
        var order = await SaveOrderAsync(NewOrder());

        await using var scopeA = Scope();
        await using var scopeB = Scope();
        var a = await Db(scopeA).Orders.SingleAsync(o => o.Id == order.Id, Ct);
        var b = await Db(scopeB).Orders.SingleAsync(o => o.Id == order.Id, Ct);

        a.StartPayment(DateTimeOffset.UtcNow);
        await Db(scopeA).SaveChangesAsync(Ct);
        b.Cancel();

        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => Db(scopeB).SaveChangesAsync(Ct));
    }

    [Theory]
    [InlineData(OrderStatus.Pending)]
    [InlineData(OrderStatus.AwaitingPayment)]
    [InlineData(OrderStatus.PaymentFailed)]
    public async Task A_second_open_order_for_the_same_hold_is_rejected(OrderStatus firstStatus)
    {
        var hold = Guid.CreateVersion7();
        var first = NewOrder(hold);
        if (firstStatus != OrderStatus.Pending)
        {
            first.StartPayment(DateTimeOffset.UtcNow);
        }

        if (firstStatus == OrderStatus.PaymentFailed)
        {
            first.MarkPaymentFailed();
        }

        await SaveOrderAsync(first);

        var violation = await ViolationAsync(() => SaveOrderAsync(NewOrder(hold)));

        violation.ConstraintName.ShouldBe("ux_orders_open_hold");
    }

    [Theory]
    [InlineData(OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Expired)]
    [InlineData(OrderStatus.Paid)]
    public async Task A_closed_order_does_not_block_a_new_order_for_the_same_hold(OrderStatus firstStatus)
    {
        var hold = Guid.CreateVersion7();
        var first = NewOrder(hold);
        switch (firstStatus)
        {
            case OrderStatus.Cancelled:
                first.Cancel();
                break;
            case OrderStatus.Expired:
                first.Expire();
                break;
            default:
                first.StartPayment(DateTimeOffset.UtcNow);
                first.MarkPaid(DateTimeOffset.UtcNow);
                break;
        }

        await SaveOrderAsync(first);

        var second = await SaveOrderAsync(NewOrder(hold));

        await using var scope = Scope();
        (await Db(scope).Orders.CountAsync(o => o.HoldId == hold, Ct)).ShouldBe(2);
        second.Status.ShouldBe(OrderStatus.Pending);
    }

    [Fact]
    public async Task A_second_valid_ticket_on_the_same_seat_is_rejected()
    {
        var first = await SaveOrderAsync(NewOrder(seats: 12));
        var second = await SaveOrderAsync(NewOrder(seats: 12));
        await SaveTicketAsync(first, 12);

        var violation = await ViolationAsync(() => SaveTicketAsync(second, 12));

        violation.ConstraintName.ShouldBe("ux_tickets_event_seat");
    }

    [Fact]
    public async Task A_voided_ticket_does_not_block_the_seat_and_a_voided_one_can_coexist_with_a_new_one()
    {
        var first = await SaveOrderAsync(NewOrder(seats: 12));
        var second = await SaveOrderAsync(NewOrder(seats: 12));
        await SaveTicketAsync(first, 12, change: t => t.Void());

        await SaveTicketAsync(second, 12);

        await using var scope = Scope();
        (await Db(scope).Tickets.CountAsync(t => t.SeatIndex == 12, Ct)).ShouldBe(2);
    }

    [Fact]
    public async Task A_ticket_cannot_be_unvoided_onto_a_seat_somebody_else_holds()
    {
        var first = await SaveOrderAsync(NewOrder(seats: 12));
        var second = await SaveOrderAsync(NewOrder(seats: 12));
        var voided = await SaveTicketAsync(first, 12, change: t => t.Void());
        await SaveTicketAsync(second, 12);

        await using var scope = Scope();
        var violation = await Should.ThrowAsync<PostgresException>(() => Db(scope).Database
            .ExecuteSqlInterpolatedAsync($"UPDATE tickets SET status = 'Valid' WHERE id = {voided.Id}", Ct));

        violation.SqlState.ShouldBe(UniqueViolation);
        violation.ConstraintName.ShouldBe("ux_tickets_event_seat");
    }

    [Fact]
    public async Task General_admission_tickets_without_a_seat_are_not_limited_and_other_events_seats_are_independent()
    {
        var order = await SaveOrderAsync(NewOrder(seats: 12));

        await SaveTicketAsync(order, null);
        await SaveTicketAsync(order, null);
        await SaveTicketAsync(order, 13);

        await using var scope = Scope();
        (await Db(scope).Tickets.CountAsync(Ct)).ShouldBe(3);
    }

    [Fact]
    public async Task A_ticket_needs_an_existing_order()
    {
        await using var scope = Scope();
        var db = Db(scope);
        db.Tickets.Add(Ticket.Issue(Guid.CreateVersion7(), _eventId, _seatedTier, 1, Guid.CreateVersion7()));

        await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task A_ticket_secret_round_trips_and_is_not_stored_in_plaintext()
    {
        var order = await SaveOrderAsync(NewOrder(seats: 12));
        var ticket = await SaveTicketAsync(order, 12);
        var secret = ticket.QrSecret.ToArray();

        await using var scope = Scope();
        var db = Db(scope);
        var loaded = await db.Tickets.AsNoTracking().SingleAsync(t => t.Id == ticket.Id, Ct);
        loaded.QrSecret.ShouldBe(secret);

        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT qr_secret FROM tickets";
        var raw = (byte[])(await command.ExecuteScalarAsync(Ct))!;
        raw.ShouldNotBe(secret);
        raw.Length.ShouldBeGreaterThan(secret.Length);
        Convert.ToHexString(raw).ShouldNotContain(Convert.ToHexString(secret));
    }

    [Fact]
    public async Task A_payment_attempt_round_trips_and_a_provider_session_id_is_unique()
    {
        var order = await SaveOrderAsync(NewOrder());
        await using (var scope = Scope())
        {
            Db(scope).PaymentAttempts.Add(PaymentAttempt.Create(order.Id, "Mock", "sess_1", order.Total, order.ExpiresAt, DateTimeOffset.UtcNow));
            await Db(scope).SaveChangesAsync(Ct);
        }

        await using (var check = Scope())
        {
            var loaded = await Db(check).PaymentAttempts.AsNoTracking().SingleAsync(Ct);
            loaded.OrderId.ShouldBe(order.Id);
            loaded.Provider.ShouldBe("Mock");
            loaded.Amount.ShouldBe(order.Total);
            loaded.Status.ShouldBe(PaymentAttemptStatus.Pending);
            loaded.ExpiresAt.ShouldBe(order.ExpiresAt - PaymentAttempt.GatewayMargin, TimeSpan.FromMilliseconds(1));
        }

        await using var duplicate = Scope();
        Db(duplicate).PaymentAttempts.Add(PaymentAttempt.Create(order.Id, "Mock", "sess_1", order.Total, order.ExpiresAt, DateTimeOffset.UtcNow));
        var violation = await ViolationAsync(() => Db(duplicate).SaveChangesAsync(Ct));
        violation.ConstraintName.ShouldBe("ux_payment_attempts_provider_session_id");
    }

    [Fact]
    public async Task A_webhook_event_id_can_be_recorded_only_once()
    {
        await using (var scope = Scope())
        {
            Db(scope).ProcessedWebhooks.Add(new ProcessedWebhook("evt_1", "Mock", DateTimeOffset.UtcNow));
            await Db(scope).SaveChangesAsync(Ct);
        }

        await using var duplicate = Scope();
        Db(duplicate).ProcessedWebhooks.Add(new ProcessedWebhook("evt_1", "Mock", DateTimeOffset.UtcNow));

        var violation = await ViolationAsync(() => Db(duplicate).SaveChangesAsync(Ct));
        violation.ConstraintName.ShouldBe("pk_processed_webhooks");
    }

    [Fact]
    public async Task A_general_admission_claim_succeeds_up_to_capacity_and_fails_beyond()
    {
        var tier = _gaTiers[0];
        await using var scope = Scope();
        var inventory = new TierInventory(Db(scope));

        (await inventory.TryClaimAsync(tier, 2, Ct)).ShouldBeTrue();
        (await inventory.TryClaimAsync(tier, 2, Ct)).ShouldBeFalse();
        (await inventory.TryClaimAsync(tier, 1, Ct)).ShouldBeTrue();
        (await inventory.TryClaimAsync(tier, 1, Ct)).ShouldBeFalse();

        (await SoldCountAsync(tier)).ShouldBe(3);
    }

    [Fact]
    public async Task A_claim_on_a_seated_or_unknown_tier_fails()
    {
        await using var scope = Scope();
        var inventory = new TierInventory(Db(scope));

        (await inventory.TryClaimAsync(_seatedTier, 1, Ct)).ShouldBeFalse();
        (await inventory.TryClaimAsync(Guid.CreateVersion7(), 1, Ct)).ShouldBeFalse();
        (await SoldCountAsync(_seatedTier)).ShouldBe(0);
    }

    [Fact]
    public async Task A_claim_needs_a_positive_quantity()
    {
        await using var scope = Scope();
        var inventory = new TierInventory(Db(scope));

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => inventory.TryClaimAsync(_gaTiers[0], 0, Ct));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => inventory.TryClaimAsync(_gaTiers[0], -1, Ct));
    }

    [Fact]
    public async Task A_claim_rolls_back_with_the_callers_transaction()
    {
        var tier = _gaTiers[0];
        await using (var scope = Scope())
        {
            var db = Db(scope);
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            (await new TierInventory(db).TryClaimAsync(tier, 3, Ct)).ShouldBeTrue();
            await transaction.RollbackAsync(Ct);
        }

        (await SoldCountAsync(tier)).ShouldBe(0);
    }

    [Fact]
    public async Task Two_concurrent_claims_for_the_last_unit_have_exactly_one_winner()
    {
        foreach (var tier in _gaTiers)
        {
            await using var setup = Scope();
            (await new TierInventory(Db(setup)).TryClaimAsync(tier, 2, Ct)).ShouldBeTrue();
        }

        foreach (var tier in _gaTiers)
        {
            var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
            {
                await using var scope = Scope();
                return await new TierInventory(Db(scope)).TryClaimAsync(tier, 1, Ct);
            }));

            results.Count(won => won).ShouldBe(1);
            (await SoldCountAsync(tier)).ShouldBe(3);
        }
    }

    [Fact]
    public async Task Editing_a_tier_does_not_reset_its_sold_count()
    {
        var tier = _gaTiers[0];
        await using (var scope = Scope())
        {
            (await new TierInventory(Db(scope)).TryClaimAsync(tier, 2, Ct)).ShouldBeTrue();
        }

        await using (var scope = Scope())
        {
            var evt = await Db(scope).Events.Include(e => e.Tiers).SingleAsync(e => e.Id == _eventId, Ct);
            evt.ReplaceTiers(evt.Tiers.Select(t => new TierDraft(t.Id, t.Name + " (sửa)", t.Price, t.Kind, t.Capacity, t.Color)).ToList());
            await Db(scope).SaveChangesAsync(Ct);
        }

        (await SoldCountAsync(tier)).ShouldBe(2);
    }

    [Fact]
    public async Task The_database_sold_source_lists_non_voided_seats_ga_counts_and_per_user_totals()
    {
        var userA = Guid.CreateVersion7();
        var userB = Guid.CreateVersion7();
        var orderA = await SaveOrderAsync(NewOrder(userId: userA, seats: [12, 13]));
        var orderB = await SaveOrderAsync(NewOrder(userId: userB, seats: [14, 15]));
        await SaveTicketAsync(orderA, 12);
        await SaveTicketAsync(orderA, 13);
        await SaveTicketAsync(orderB, 14);
        await SaveTicketAsync(orderB, 15, change: t => t.Void());
        await SaveTicketAsync(orderB, null, _gaTiers[0]);
        await SaveTicketAsync(orderB, null, _gaTiers[0]);
        await SaveTicketAsync(orderA, null, _gaTiers[1]);

        await using var scope = Scope();
        var sold = await new DbSoldInventorySource(Db(scope)).GetAsync(_eventId, Ct);

        sold.Seats.Order().ShouldBe([12, 13, 14]);
        sold.GaSold.ShouldBe(new Dictionary<Guid, int> { [_gaTiers[0]] = 2, [_gaTiers[1]] = 1 }, ignoreOrder: true);
        sold.BoughtByUser.ShouldBe(new Dictionary<Guid, int> { [userA] = 3, [userB] = 3 }, ignoreOrder: true);
    }

    [Fact]
    public async Task The_database_sold_source_is_empty_for_an_event_without_tickets()
    {
        await using var scope = Scope();

        var sold = await new DbSoldInventorySource(Db(scope)).GetAsync(Guid.CreateVersion7(), Ct);

        sold.Seats.ShouldBeEmpty();
        sold.GaSold.ShouldBeEmpty();
        sold.BoughtByUser.ShouldBeEmpty();
    }
}
