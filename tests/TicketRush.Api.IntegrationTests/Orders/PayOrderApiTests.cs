using System.Net;
using System.Reflection;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TicketRush.Api.IntegrationTests.Auth;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Application.Abstractions.Inventory;
using TicketRush.Application.Abstractions.Payments;
using TicketRush.Domain.Events;
using TicketRush.Domain.Orders;
using TicketRush.Domain.Primitives;
using TicketRush.Infrastructure.Identity;
using TicketRush.Infrastructure.Payments;
using TicketRush.Infrastructure.Persistence;
using TicketRush.Infrastructure.Persistence.Seed;
using Order = TicketRush.Domain.Orders.Order;

namespace TicketRush.Api.IntegrationTests.Orders;

/// <summary>Spec 08 <c>POST /orders/{id}/pay</c> over HTTP: one attempt per order whatever the clicks, honest failures, holds untouched on an outage.</summary>
public sealed class PayOrderApiTests(ContainersFixture containers) : IAsyncLifetime
{
    private static readonly Dictionary<string, string?> FastBreaker = new()
    {
        ["Payments:Resilience:RetryCount"] = "1",
        ["Payments:Resilience:RetryBaseDelay"] = "00:00:00",
        ["Payments:Resilience:BreakerMinimumThroughput"] = "2",
        ["Payments:Resilience:BreakerBreakDuration"] = "00:00:01",
    };

    private ApiFactory _factory = null!;
    private WebApplicationFactory<Program> _spyHost = null!;
    private SpyGateway _spy = null!;
    private HttpClient _client = null!;
    private HttpClient _authClient = null!;
    private AuthApi _auth = null!;
    private Guid _event;
    private Guid[] _gaTiers = [];
    private Guid _gaTier;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _factory = new ApiFactory(await containers.CreateDatabaseAsync(), containers.RedisConnectionString, settings: FastBreaker);
        _authClient = AuthApi.CreateClient(_factory);
        _auth = new AuthApi(_factory, _authClient);
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DevDataSeeder>().SeedAsync(Ct);
        var evt = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Events.AsNoTracking().Include(e => e.Tiers)
            .SingleAsync(e => e.Slug == "son-tung-mtp-live", Ct);
        _event = evt.Id;
        _gaTiers = [.. evt.Tiers.Where(t => t.Kind == TicketKind.GeneralAdmission).Select(t => t.Id)];
        _gaTier = _gaTiers[0];
        await scope.ServiceProvider.GetRequiredService<IInventorySeeder>().SeedAsync(_event, Ct);

        // A second host over the same database and Redis whose gateway is watched (and can be told to misbehave).
        _spy = new SpyGateway();
        _spyHost = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddSingleton<IPaymentGatewayResolver>(sp => new SpyResolver(ActivatorUtilities.CreateInstance<PaymentGatewayResolver>(sp), _spy))));
        _client = _spyHost.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        _authClient.Dispose();
        await _spyHost.DisposeAsync();
        await _factory.DisposeAsync();
    }

    private sealed record Buyer(Guid UserId, string Access);

    private sealed record Placed(Buyer Buyer, Guid OrderId, Guid HoldId, long Total, DateTimeOffset ExpiresAt);

    private IInventoryStore Store => _factory.Services.GetRequiredService<IInventoryStore>();

    private async Task<Buyer> NewBuyerAsync()
    {
        var email = $"buyer-{Guid.NewGuid():N}@example.com";
        var access = (await _auth.SignInWithRoleAsync(email, "Customer")).AccessToken;
        using var scope = _factory.Services.CreateScope();
        var user = await scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>().FindByEmailAsync(email);
        return new Buyer(user!.Id, access);
    }

    /// <summary>A buyer with a live hold of two GA tickets and a Pending order for it (what checkout has when the buyer presses "pay").</summary>
    private async Task<Placed> PlaceOrderAsync(TimeSpan? holdTtl = null)
    {
        var buyer = await NewBuyerAsync();
        var request = new HoldRequest(_event, buyer.UserId, [], [new GaLine(_gaTier, 2)], _gaTiers, 8, false, DateTimeOffset.UtcNow, holdTtl ?? TimeSpan.FromMinutes(8));
        (await Store.CreateHoldAsync(request, Ct)).Status.ShouldBe(HoldStatus.Ok);
        var hold = (await Store.GetUserHoldAsync(_event, buyer.UserId, Ct))!;
        var body = new { eventId = _event, holdId = hold.HoldId, buyer = new { name = "Nguyễn Văn An", email = "an@example.com", phone = "0901234567" } };
        var order = await JsonAsync(await SendAsync(HttpMethod.Post, "/api/v1/orders", buyer.Access, body, Guid.NewGuid().ToString()), HttpStatusCode.Created);
        return new Placed(buyer, order.GetProperty("id").GetGuid(), hold.HoldId, order.GetProperty("total").GetInt64(), order.GetProperty("expiresAt").GetDateTimeOffset());
    }

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

    private Task<HttpResponseMessage> PayAsync(Placed placed, string? key = null, object? body = null, Buyer? payer = null) =>
        SendAsync(HttpMethod.Post, $"/api/v1/orders/{placed.OrderId}/pay", (payer ?? placed.Buyer).Access, body, key ?? Guid.NewGuid().ToString());

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

    private Task<List<PaymentAttempt>> AttemptsAsync(Guid orderId) =>
        WithDbAsync(db => db.PaymentAttempts.AsNoTracking().Where(a => a.OrderId == orderId).OrderBy(a => a.CreatedAt).ToListAsync(Ct));

    private Task<OrderStatus> StatusAsync(Guid orderId) =>
        WithDbAsync(async db => (await db.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId, Ct)).Status);

    private Task<int> SetOrderExpiryAsync(Guid orderId, DateTimeOffset expiresAt) =>
        WithDbAsync(db => db.Database.ExecuteSqlAsync($"UPDATE orders SET expires_at = {expiresAt} WHERE id = {orderId}", Ct));

    /// <summary>What the gateway reported failed: the attempt and the order, like the webhook (B6) will do.</summary>
    private async Task FailPaymentAsync(Guid orderId) => await WithDbAsync(async db =>
    {
        var order = await db.Orders.SingleAsync(o => o.Id == orderId, Ct);
        var attempt = await db.PaymentAttempts.SingleAsync(a => a.OrderId == orderId && a.Status == PaymentAttemptStatus.Pending, Ct);
        attempt.MarkFailed();
        order.MarkPaymentFailed();
        await db.SaveChangesAsync(Ct);
        return 0;
    });

    private async Task ForceStatusAsync(Guid orderId, OrderStatus target) => await WithDbAsync(async db =>
    {
        var order = await db.Orders.SingleAsync(o => o.Id == orderId, Ct);
        var now = DateTimeOffset.UtcNow;
        switch (target)
        {
            case OrderStatus.Cancelled:
                order.Cancel();
                break;
            case OrderStatus.Expired:
                order.Expire();
                break;
            case OrderStatus.Paid:
                StartIfPending(order, now);
                order.MarkPaid(now);
                break;
            case OrderStatus.RefundPending:
                StartIfPending(order, now);
                order.MarkPaid(now);
                order.RequestRefundForCancelledEvent();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(target));
        }

        await db.SaveChangesAsync(Ct);
        return 0;
    });

    private static void StartIfPending(Order order, DateTimeOffset now)
    {
        if (order.Status == OrderStatus.Pending)
        {
            order.StartPayment(now);
        }
    }

    private static string SessionIdOf(string redirectUrl) => new Uri(redirectUrl).Segments[^1];

    private Task<MockSession?> MockSessionAsync(string redirectUrl) =>
        _factory.Services.GetRequiredService<MockSessionStore>().GetAsync(SessionIdOf(redirectUrl), Ct);

    private MockPaymentGateway MockGateway => _spyHost.Services.GetRequiredService<MockPaymentGateway>();

    // ---- happy path ----

    [Fact]
    public async Task Paying_a_pending_order_opens_a_mock_session_and_awaits_payment()
    {
        var placed = await PlaceOrderAsync();

        var pay = await JsonAsync(await PayAsync(placed));

        var redirect = pay.GetProperty("redirectUrl").GetString()!;
        var attempts = await AttemptsAsync(placed.OrderId);
        var attempt = attempts.ShouldHaveSingleItem();
        redirect.ShouldBe($"http://localhost:3000/mock-gateway/{attempt.ProviderSessionId}");
        pay.GetProperty("attemptId").GetGuid().ShouldBe(attempt.Id);
        pay.GetProperty("expiresAt").GetDateTimeOffset().ShouldBe(placed.ExpiresAt - PaymentAttempt.GatewayMargin, TimeSpan.FromMilliseconds(5));
        pay.GetProperty("serverNow").GetDateTimeOffset().ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-1));
        attempt.Provider.ShouldBe("mock");
        attempt.Status.ShouldBe(PaymentAttemptStatus.Pending);
        attempt.Amount.ShouldBe(new Money(placed.Total));
        attempt.RedirectUrl.ShouldBe(redirect);
        (await StatusAsync(placed.OrderId)).ShouldBe(OrderStatus.AwaitingPayment);
        (await MockSessionAsync(redirect))!.ExpiresAt.ShouldBe(attempt.ExpiresAt, TimeSpan.FromMilliseconds(5));
    }

    [Fact]
    public async Task The_return_url_is_built_by_the_server_from_the_configured_web_origin()
    {
        var placed = await PlaceOrderAsync();

        var pay = await JsonAsync(await PayAsync(placed));

        var session = await MockSessionAsync(pay.GetProperty("redirectUrl").GetString()!);
        session.ShouldNotBeNull();
        session.ReturnUrl.ToString().ShouldBe($"http://localhost:3000/checkout/{placed.OrderId}/result");
        session.OrderId.ShouldBe(placed.OrderId);
        session.Amount.ShouldBe(new Money(placed.Total));
    }

    [Theory]
    [InlineData("{\"returnUrl\":\"https://evil.example/steal\"}")]
    [InlineData("{\"returnUrl\":\"//evil.example\",\"redirectUrl\":\"https://evil.example\"}")]
    public async Task A_return_url_in_the_body_is_ignored(string json)
    {
        var placed = await PlaceOrderAsync();
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/orders/{placed.OrderId}/pay") { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", placed.Buyer.Access);
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var pay = await JsonAsync(await _client.SendAsync(request, Ct));

        var redirect = pay.GetProperty("redirectUrl").GetString()!;
        redirect.ShouldStartWith("http://localhost:3000/mock-gateway/");
        (await MockSessionAsync(redirect))!.ReturnUrl.ToString().ShouldBe($"http://localhost:3000/checkout/{placed.OrderId}/result");
    }

    // ---- one attempt, however many clicks ----

    [Fact]
    public async Task The_same_key_twice_replays_the_answer_and_makes_one_attempt()
    {
        var placed = await PlaceOrderAsync();
        var key = Guid.NewGuid().ToString();

        var first = await JsonAsync(await PayAsync(placed, key));
        var second = await JsonAsync(await PayAsync(placed, key));

        second.GetProperty("redirectUrl").GetString().ShouldBe(first.GetProperty("redirectUrl").GetString());
        second.GetProperty("attemptId").GetGuid().ShouldBe(first.GetProperty("attemptId").GetGuid());
        (await AttemptsAsync(placed.OrderId)).Count.ShouldBe(1);
        _spy.CreateCalls.ShouldBe(1);
    }

    [Fact]
    public async Task A_new_key_reuses_the_live_attempt_instead_of_opening_another_session()
    {
        var placed = await PlaceOrderAsync();
        var first = await JsonAsync(await PayAsync(placed));

        var again = await JsonAsync(await PayAsync(placed));

        again.GetProperty("redirectUrl").GetString().ShouldBe(first.GetProperty("redirectUrl").GetString());
        again.GetProperty("attemptId").GetGuid().ShouldBe(first.GetProperty("attemptId").GetGuid());
        (await AttemptsAsync(placed.OrderId)).Count.ShouldBe(1);
        _spy.CreateCalls.ShouldBe(1);
    }

    [Fact]
    public async Task A_double_click_with_the_same_key_creates_one_attempt_and_one_url()
    {
        var placed = await PlaceOrderAsync();
        var key = Guid.NewGuid().ToString();

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => PayAsync(placed, key)));

        var urls = new HashSet<string>();
        foreach (var response in responses)
        {
            // The filter answers a click that arrives while the first still runs with 409 in_progress; the rest replay the first answer.
            response.StatusCode.ShouldBeOneOf(HttpStatusCode.OK, HttpStatusCode.Conflict);
            var body = await JsonAsync(response, response.StatusCode);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                urls.Add(body.GetProperty("redirectUrl").GetString()!);
            }
            else
            {
                body.GetProperty("code").GetString().ShouldBe("idempotency.in_progress");
            }
        }

        urls.Count.ShouldBe(1);
        (await AttemptsAsync(placed.OrderId)).Count.ShouldBe(1);
        _spy.CreateCalls.ShouldBe(1);
        var replay = await JsonAsync(await PayAsync(placed, key));
        replay.GetProperty("redirectUrl").GetString().ShouldBe(urls.Single());
    }

    [Fact]
    public async Task Different_keys_at_the_same_moment_still_make_one_attempt_and_one_url()
    {
        var placed = await PlaceOrderAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => PayAsync(placed)));

        var urls = new HashSet<string>();
        foreach (var response in responses)
        {
            urls.Add((await JsonAsync(response)).GetProperty("redirectUrl").GetString()!);
        }

        urls.Count.ShouldBe(1);
        (await AttemptsAsync(placed.OrderId)).Count.ShouldBe(1);
        (await StatusAsync(placed.OrderId)).ShouldBe(OrderStatus.AwaitingPayment);
    }

    [Fact]
    public async Task Losing_the_race_to_a_concurrent_pay_returns_the_winners_url()
    {
        var placed = await PlaceOrderAsync();
        string? winnerUrl = null;
        _spy.AfterCreate = async _ =>
        {
            // Another tab pressed "pay" after this request opened its session and before it saved.
            _spy.AfterCreate = null;
            winnerUrl = (await JsonAsync(await PayAsync(placed))).GetProperty("redirectUrl").GetString();
        };

        var pay = await JsonAsync(await PayAsync(placed));

        winnerUrl.ShouldNotBeNull();
        pay.GetProperty("redirectUrl").GetString().ShouldBe(winnerUrl);
        (await AttemptsAsync(placed.OrderId)).Count.ShouldBe(1);
        (await StatusAsync(placed.OrderId)).ShouldBe(OrderStatus.AwaitingPayment);
    }

    [Fact]
    public async Task A_session_opened_before_a_crash_is_picked_up_again_by_the_retry()
    {
        var placed = await PlaceOrderAsync();
        var orphan = await OpenOrphanSessionAsync(placed, attempt: 1);

        var pay = await JsonAsync(await PayAsync(placed));

        pay.GetProperty("redirectUrl").GetString().ShouldBe(orphan.RedirectUrl.AbsoluteUri);
        (await AttemptsAsync(placed.OrderId)).ShouldHaveSingleItem().ProviderSessionId.ShouldBe(orphan.SessionId);
    }

    /// <summary>The state a crash between "gateway session created" and "attempt saved" leaves behind: a session nobody recorded.</summary>
    private async Task<PaymentSession> OpenOrphanSessionAsync(Placed placed, int attempt)
    {
        var order = await WithDbAsync(db => db.Orders.AsNoTracking().SingleAsync(o => o.Id == placed.OrderId, Ct));
        var gateway = _spyHost.Services.GetRequiredService<IEnumerable<IPaymentGateway>>().Single(g => g.Provider == "mock");
        return await gateway.CreateSessionAsync(
            order,
            new Uri($"http://localhost:3000/checkout/{placed.OrderId}/result"),
            order.ExpiresAt - PaymentAttempt.GatewayMargin,
            $"order:{placed.OrderId:N}:attempt:{attempt}",
            Ct);
    }

    // ---- retry after a failed payment ----

    [Fact]
    public async Task Paying_again_after_a_failed_payment_opens_a_new_session_and_keeps_the_failed_attempt()
    {
        var placed = await PlaceOrderAsync();
        var first = await JsonAsync(await PayAsync(placed));
        await FailPaymentAsync(placed.OrderId);
        (await StatusAsync(placed.OrderId)).ShouldBe(OrderStatus.PaymentFailed);

        var second = await JsonAsync(await PayAsync(placed));

        second.GetProperty("redirectUrl").GetString().ShouldNotBe(first.GetProperty("redirectUrl").GetString());
        var attempts = await AttemptsAsync(placed.OrderId);
        attempts.Select(a => a.Status).ShouldBe([PaymentAttemptStatus.Failed, PaymentAttemptStatus.Pending]);
        attempts[1].Id.ShouldBe(second.GetProperty("attemptId").GetGuid());
        (await StatusAsync(placed.OrderId)).ShouldBe(OrderStatus.AwaitingPayment);
    }

    [Fact]
    public async Task An_awaiting_order_whose_attempt_failed_gets_a_new_attempt_without_a_second_state_change()
    {
        var placed = await PlaceOrderAsync();
        await JsonAsync(await PayAsync(placed));
        await WithDbAsync(async db =>
        {
            (await db.PaymentAttempts.SingleAsync(a => a.OrderId == placed.OrderId, Ct)).MarkFailed();
            await db.SaveChangesAsync(Ct);
            return 0;
        });

        var pay = await JsonAsync(await PayAsync(placed));

        (await AttemptsAsync(placed.OrderId)).Select(a => a.Status).ShouldBe([PaymentAttemptStatus.Failed, PaymentAttemptStatus.Pending]);
        pay.GetProperty("attemptId").GetGuid().ShouldBe((await AttemptsAsync(placed.OrderId))[1].Id);
        (await StatusAsync(placed.OrderId)).ShouldBe(OrderStatus.AwaitingPayment);
    }

    // ---- refusals ----

    [Fact]
    public async Task An_order_past_its_expiry_is_order_expired_and_no_session_is_opened()
    {
        var placed = await PlaceOrderAsync();
        await SetOrderExpiryAsync(placed.OrderId, DateTimeOffset.UtcNow.AddSeconds(-5));

        await ShouldFailWithAsync(await PayAsync(placed), HttpStatusCode.Conflict, "order.expired");

        _spy.CreateCalls.ShouldBe(0);
        (await AttemptsAsync(placed.OrderId)).ShouldBeEmpty();
        (await StatusAsync(placed.OrderId)).ShouldBe(OrderStatus.Pending);
    }

    [Fact]
    public async Task Too_little_time_for_a_payment_window_is_order_expired()
    {
        var placed = await PlaceOrderAsync();
        await SetOrderExpiryAsync(placed.OrderId, DateTimeOffset.UtcNow.AddSeconds(20));

        await ShouldFailWithAsync(await PayAsync(placed), HttpStatusCode.Conflict, "order.expired");

        _spy.CreateCalls.ShouldBe(0);
        (await StatusAsync(placed.OrderId)).ShouldBe(OrderStatus.Pending);
    }

    [Fact]
    public async Task A_live_attempt_stops_being_reusable_when_the_order_runs_out_of_time()
    {
        var placed = await PlaceOrderAsync();
        await JsonAsync(await PayAsync(placed));
        await SetOrderExpiryAsync(placed.OrderId, DateTimeOffset.UtcNow.AddSeconds(-1));

        await ShouldFailWithAsync(await PayAsync(placed), HttpStatusCode.Conflict, "order.expired");
    }

    [Theory]
    [InlineData(OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Paid)]
    [InlineData(OrderStatus.RefundPending)]
    public async Task An_order_in_a_closed_state_is_not_payable_and_opens_no_session(OrderStatus status)
    {
        var placed = await PlaceOrderAsync();
        await ForceStatusAsync(placed.OrderId, status);

        await ShouldFailWithAsync(await PayAsync(placed), HttpStatusCode.Conflict, "order.not_payable");

        _spy.CreateCalls.ShouldBe(0);
        (await AttemptsAsync(placed.OrderId)).ShouldBeEmpty();
        (await StatusAsync(placed.OrderId)).ShouldBe(status);
    }

    [Fact]
    public async Task An_order_the_sweep_already_expired_says_order_expired_like_one_it_has_not_reached_yet()
    {
        var placed = await PlaceOrderAsync();
        await ForceStatusAsync(placed.OrderId, OrderStatus.Expired);

        await ShouldFailWithAsync(await PayAsync(placed), HttpStatusCode.Conflict, "order.expired");

        _spy.CreateCalls.ShouldBe(0);
        (await AttemptsAsync(placed.OrderId)).ShouldBeEmpty();
        (await StatusAsync(placed.OrderId)).ShouldBe(OrderStatus.Expired);
    }

    [Fact]
    public async Task A_cancelled_event_is_checked_in_the_database_and_no_session_is_opened()
    {
        var placed = await PlaceOrderAsync();
        await WithDbAsync(db => db.Database.ExecuteSqlAsync($"UPDATE events SET status = 'Cancelled' WHERE id = {_event}", Ct));

        await ShouldFailWithAsync(await PayAsync(placed), HttpStatusCode.Conflict, "order.not_payable");

        _spy.CreateCalls.ShouldBe(0);
        (await AttemptsAsync(placed.OrderId)).ShouldBeEmpty();
        (await StatusAsync(placed.OrderId)).ShouldBe(OrderStatus.Pending);
    }

    [Fact]
    public async Task A_dead_hold_is_refused_as_hold_expired_and_no_session_is_opened()
    {
        var placed = await PlaceOrderAsync();
        await Store.ReleaseHoldAsync(_event, placed.HoldId, _gaTiers, Ct);

        await ShouldFailWithAsync(await PayAsync(placed), HttpStatusCode.Conflict, "order.hold_expired");

        _spy.CreateCalls.ShouldBe(0);
        (await AttemptsAsync(placed.OrderId)).ShouldBeEmpty();
        (await StatusAsync(placed.OrderId)).ShouldBe(OrderStatus.Pending);
    }

    [Fact]
    public async Task Another_buyers_order_and_an_unknown_order_are_404()
    {
        var placed = await PlaceOrderAsync();
        var stranger = await NewBuyerAsync();

        await ShouldFailWithAsync(await PayAsync(placed, payer: stranger), HttpStatusCode.NotFound, "order.not_found");
        await ShouldFailWithAsync(
            await SendAsync(HttpMethod.Post, $"/api/v1/orders/{Guid.NewGuid()}/pay", stranger.Access, key: Guid.NewGuid().ToString()),
            HttpStatusCode.NotFound,
            "order.not_found");

        _spy.CreateCalls.ShouldBe(0);
        (await StatusAsync(placed.OrderId)).ShouldBe(OrderStatus.Pending);
    }

    [Fact]
    public async Task Paying_needs_a_signed_in_user_and_an_idempotency_key()
    {
        var placed = await PlaceOrderAsync();

        var anonymous = await SendAsync(HttpMethod.Post, $"/api/v1/orders/{placed.OrderId}/pay", null, key: Guid.NewGuid().ToString());
        var keyless = await SendAsync(HttpMethod.Post, $"/api/v1/orders/{placed.OrderId}/pay", placed.Buyer.Access);

        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await ShouldFailWithAsync(keyless, HttpStatusCode.BadRequest, "idempotency.key_required");
        _spy.CreateCalls.ShouldBe(0);
    }

    [Fact]
    public async Task A_gateway_that_refuses_the_session_is_payment_rejected_and_nothing_changes()
    {
        var placed = await PlaceOrderAsync();
        _spy.Failure = new PaymentRejectedException("no");

        await ShouldFailWithAsync(await PayAsync(placed), HttpStatusCode.Conflict, "payment.rejected");

        (await AttemptsAsync(placed.OrderId)).ShouldBeEmpty();
        (await StatusAsync(placed.OrderId)).ShouldBe(OrderStatus.Pending);
    }


    // ---- the order changes while the session is being opened ----

    [Theory]
    [InlineData(OrderStatus.Paid, "order.not_payable")]
    [InlineData(OrderStatus.Expired, "order.expired")]
    public async Task An_awaiting_order_that_changes_during_the_call_gets_no_new_attempt(OrderStatus becomes, string code)
    {
        var placed = await AwaitingWithFailedAttemptAsync();
        _spy.AfterCreate = async _ =>
        {
            // A webhook or the expiry sweep got to the order after this request read it and before it saved.
            _spy.AfterCreate = null;
            await ForceStatusAsync(placed.OrderId, becomes);
        };

        await ShouldFailWithAsync(await PayAsync(placed), HttpStatusCode.Conflict, code);

        _spy.CreateCalls.ShouldBe(1);
        (await AttemptsAsync(placed.OrderId)).ShouldHaveSingleItem().Status.ShouldBe(PaymentAttemptStatus.Failed);
        (await StatusAsync(placed.OrderId)).ShouldBe(becomes);
    }

    [Fact]
    public async Task Losing_only_the_unique_index_race_returns_the_winners_attempt()
    {
        var placed = await AwaitingWithFailedAttemptAsync();
        Guid winner = Guid.Empty;
        _spy.AfterCreate = async session =>
        {
            // Another request recorded the very same session and left the order untouched.
            _spy.AfterCreate = null;
            winner = await RecordAttemptAsync(placed, session);
        };

        var pay = await JsonAsync(await PayAsync(placed));

        winner.ShouldNotBe(Guid.Empty);
        pay.GetProperty("attemptId").GetGuid().ShouldBe(winner);
        (await AttemptsAsync(placed.OrderId)).Count.ShouldBe(2);
        (await StatusAsync(placed.OrderId)).ShouldBe(OrderStatus.AwaitingPayment);
    }

    /// <summary>AwaitingPayment whose only attempt failed: the path where the handler does not change the order itself.</summary>
    private async Task<Placed> AwaitingWithFailedAttemptAsync()
    {
        var placed = await PlaceOrderAsync();
        await JsonAsync(await PayAsync(placed));
        await WithDbAsync(async db =>
        {
            (await db.PaymentAttempts.SingleAsync(a => a.OrderId == placed.OrderId, Ct)).MarkFailed();
            await db.SaveChangesAsync(Ct);
            return 0;
        });
        _spy.Reset();
        return placed;
    }

    private Task<Guid> RecordAttemptAsync(Placed placed, PaymentSession session) => WithDbAsync(async db =>
    {
        var attempt = PaymentAttempt.Create(
            placed.OrderId, "mock", session.SessionId, session.RedirectUrl.AbsoluteUri, new Money(placed.Total), placed.ExpiresAt, DateTimeOffset.UtcNow);
        db.PaymentAttempts.Add(attempt);
        await db.SaveChangesAsync(Ct);
        return attempt.Id;
    });

    // ---- the clock moves while the gateway is called ----

    private (WebApplicationFactory<Program> Host, HttpClient Client, FakeTimeProvider Time) FakeClockHost()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var host = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.AddSingleton<TimeProvider>(time);
            s.AddSingleton<IPaymentGatewayResolver>(sp => new SpyResolver(ActivatorUtilities.CreateInstance<PaymentGatewayResolver>(sp), _spy));
        }));
        return (host, host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false }), time);
    }

    [Fact]
    public async Task An_order_that_runs_out_of_time_while_the_session_opens_is_order_expired_and_no_attempt_is_written()
    {
        var placed = await PlaceOrderAsync();
        var (host, client, time) = FakeClockHost();
        await using var hostLease = host;
        using var clientLease = client;
        _spy.AfterCreate = _ =>
        {
            time.Advance(TimeSpan.FromMinutes(9));
            return Task.CompletedTask;
        };

        var response = await SendAsync(HttpMethod.Post, $"/api/v1/orders/{placed.OrderId}/pay", placed.Buyer.Access, key: Guid.NewGuid().ToString(), client: client);

        await ShouldFailWithAsync(response, HttpStatusCode.Conflict, "order.expired");
        _spy.CreateCalls.ShouldBe(1);
        (await AttemptsAsync(placed.OrderId)).ShouldBeEmpty();
        (await StatusAsync(placed.OrderId)).ShouldBe(OrderStatus.Pending);
    }

    [Fact]
    public async Task The_attempt_and_the_response_use_the_time_after_the_session_opened()
    {
        var placed = await PlaceOrderAsync();
        var (host, client, time) = FakeClockHost();
        await using var hostLease = host;
        using var clientLease = client;
        var before = time.GetUtcNow();
        _spy.AfterCreate = _ =>
        {
            time.Advance(TimeSpan.FromSeconds(20));
            return Task.CompletedTask;
        };

        var pay = await JsonAsync(await SendAsync(HttpMethod.Post, $"/api/v1/orders/{placed.OrderId}/pay", placed.Buyer.Access, key: Guid.NewGuid().ToString(), client: client));

        pay.GetProperty("serverNow").GetDateTimeOffset().ShouldBe(before.AddSeconds(20));
        (await AttemptsAsync(placed.OrderId)).ShouldHaveSingleItem().CreatedAt.ShouldBe(before.AddSeconds(20), TimeSpan.FromMilliseconds(1));
    }

    // ---- more refusals and the stale attempt ----

    [Fact]
    public async Task A_hold_replaced_by_a_newer_one_no_longer_backs_the_order()
    {
        var placed = await PlaceOrderAsync();
        var newer = new HoldRequest(_event, placed.Buyer.UserId, [], [new GaLine(_gaTier, 1)], _gaTiers, 8, true, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(8));
        (await Store.CreateHoldAsync(newer, Ct)).Status.ShouldBe(HoldStatus.Ok);

        await ShouldFailWithAsync(await PayAsync(placed), HttpStatusCode.Conflict, "order.hold_expired");

        _spy.CreateCalls.ShouldBe(0);
        (await StatusAsync(placed.OrderId)).ShouldBe(OrderStatus.Pending);
    }

    [Fact]
    public async Task A_hold_that_ran_out_by_time_is_hold_expired_even_while_the_order_row_still_has_time()
    {
        var placed = await PlaceOrderAsync(holdTtl: TimeSpan.FromSeconds(2));
        await SetOrderExpiryAsync(placed.OrderId, DateTimeOffset.UtcNow.AddMinutes(8));
        await Task.Delay(TimeSpan.FromSeconds(2.5), Ct);

        await ShouldFailWithAsync(await PayAsync(placed), HttpStatusCode.Conflict, "order.hold_expired");

        _spy.CreateCalls.ShouldBe(0);
        (await StatusAsync(placed.OrderId)).ShouldBe(OrderStatus.Pending);
    }

    [Fact]
    public async Task Redis_being_down_while_the_hold_is_checked_is_system_busy_and_changes_nothing()
    {
        var placed = await PlaceOrderAsync();
        var proxy = DispatchProxy.Create<IInventoryStore, SwitchableStore>();
        var board = new Switchboard { Down = true };
        ((SwitchableStore)(object)proxy).Setup(Store, board);
        await using var host = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton(proxy)));
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var response = await SendAsync(HttpMethod.Post, $"/api/v1/orders/{placed.OrderId}/pay", placed.Buyer.Access, key: Guid.NewGuid().ToString(), client: client);

        await ShouldFailWithAsync(response, HttpStatusCode.ServiceUnavailable, "system.busy");
        (await AttemptsAsync(placed.OrderId)).ShouldBeEmpty();
        (await StatusAsync(placed.OrderId)).ShouldBe(OrderStatus.Pending);
    }

    [Fact]
    public async Task An_awaiting_order_whose_attempt_ran_out_of_time_gets_a_new_attempt_with_a_matching_session()
    {
        var placed = await PlaceOrderAsync();
        var first = await JsonAsync(await PayAsync(placed));
        await WithDbAsync(db => db.Database.ExecuteSqlAsync(
            $"UPDATE payment_attempts SET expires_at = {DateTimeOffset.UtcNow.AddMinutes(-1)} WHERE order_id = {placed.OrderId}", Ct));

        var pay = await JsonAsync(await PayAsync(placed));

        pay.GetProperty("redirectUrl").GetString().ShouldNotBe(first.GetProperty("redirectUrl").GetString());
        var attempts = await AttemptsAsync(placed.OrderId);
        attempts.Count.ShouldBe(2);
        var current = attempts.Single(a => a.Id == pay.GetProperty("attemptId").GetGuid());
        (await MockSessionAsync(current.RedirectUrl))!.ExpiresAt.ShouldBe(current.ExpiresAt, TimeSpan.FromMilliseconds(5));
        (await StatusAsync(placed.OrderId)).ShouldBe(OrderStatus.AwaitingPayment);
    }

    // ---- gateway outage ----

    [Fact]
    public async Task A_gateway_outage_is_503_and_leaves_the_order_pending_and_the_hold_alive_then_recovers()
    {
        var placed = await PlaceOrderAsync();
        var key = Guid.NewGuid().ToString();
        MockGateway.SimulateOutage = true;

        var down = await PayAsync(placed, key);

        var problem = await JsonAsync(down, HttpStatusCode.ServiceUnavailable);
        problem.GetProperty("code").GetString().ShouldBe("payment.gateway_unavailable");
        await AssertUntouchedAsync(placed);

        // The breaker is open now: even with the gateway back, calls fail fast until the break is over.
        MockGateway.SimulateOutage = false;
        await ShouldFailWithAsync(await PayAsync(placed), HttpStatusCode.ServiceUnavailable, "payment.gateway_unavailable");
        await AssertUntouchedAsync(placed);

        await Task.Delay(TimeSpan.FromSeconds(1.5), Ct);
        var recovered = await JsonAsync(await PayAsync(placed, key));

        recovered.GetProperty("redirectUrl").GetString().ShouldStartWith("http://localhost:3000/mock-gateway/");
        (await AttemptsAsync(placed.OrderId)).Count.ShouldBe(1);
        (await StatusAsync(placed.OrderId)).ShouldBe(OrderStatus.AwaitingPayment);
    }

    private async Task AssertUntouchedAsync(Placed placed)
    {
        (await StatusAsync(placed.OrderId)).ShouldBe(OrderStatus.Pending);
        (await AttemptsAsync(placed.OrderId)).ShouldBeEmpty();
        var hold = await Store.GetUserHoldAsync(_event, placed.Buyer.UserId, Ct);
        hold.ShouldNotBeNull();
        hold.HoldId.ShouldBe(placed.HoldId);
        hold.ExpiresAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow);
    }

    // ---- test doubles ----

    private sealed class SpyResolver(IPaymentGatewayResolver inner, SpyGateway spy) : IPaymentGatewayResolver
    {
        public IPaymentGateway Default { get; } = new SpyGatewayAdapter(inner.Default, spy);

        public IPaymentGateway? Get(string provider) => inner.Get(provider);
    }

    /// <summary>Settings of the watcher: how often sessions were requested, what to throw, and what to do right after one was opened.</summary>
    private sealed class SpyGateway
    {
        private int _createCalls;

        public int CreateCalls => Volatile.Read(ref _createCalls);

        public Exception? Failure { get; set; }

        public Func<PaymentSession, Task>? AfterCreate { get; set; }

        public void Reset() => Interlocked.Exchange(ref _createCalls, 0);

        public void Count() => Interlocked.Increment(ref _createCalls);
    }

    private sealed class SpyGatewayAdapter(IPaymentGateway inner, SpyGateway spy) : IPaymentGateway
    {
        public string Provider => inner.Provider;

        public async Task<PaymentSession> CreateSessionAsync(Order order, Uri returnUrl, DateTimeOffset expiresAt, string idempotencyKey, CancellationToken ct)
        {
            spy.Count();
            if (spy.Failure is { } failure)
            {
                throw failure;
            }

            var session = await inner.CreateSessionAsync(order, returnUrl, expiresAt, idempotencyKey, ct);
            if (spy.AfterCreate is { } after)
            {
                await after(session);
            }

            return session;
        }

        public Task<PaymentStatus> GetStatusAsync(string sessionId, CancellationToken ct) => inner.GetStatusAsync(sessionId, ct);

        public Task<RefundResult> RefundAsync(string sessionId, Money amount, string idempotencyKey, CancellationToken ct) =>
            inner.RefundAsync(sessionId, amount, idempotencyKey, ct);

        public WebhookEvent VerifyAndParse(WebhookHeaders headers, string body) => inner.VerifyAndParse(headers, body);
    }

    public sealed class Switchboard
    {
        public bool Down { get; set; }
    }

    /// <summary>Wraps the real inventory so the hold lookup fails like a Redis outage.</summary>
    public class SwitchableStore : DispatchProxy
    {
        private IInventoryStore _inner = null!;
        private Switchboard _board = null!;

        internal void Setup(IInventoryStore inner, Switchboard board) => (_inner, _board) = (inner, board);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (_board.Down && targetMethod!.Name == nameof(IInventoryStore.GetUserHoldAsync))
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
