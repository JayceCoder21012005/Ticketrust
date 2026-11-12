using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TicketRush.Api.IntegrationTests.Auth;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Application.Abstractions.Notifications;
using TicketRush.Infrastructure.Realtime;

namespace TicketRush.Api.IntegrationTests.Notifications;

/// <summary>
/// The user's live channel (spec 09) with real SignalR clients against the test server: only the owner's tabs receive a push,
/// the JSON is the shape the web client reads, and a push sent from another host (the worker) arrives through the Redis backplane.
/// </summary>
public sealed class UserHubTests(ContainersFixture containers) : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(1);
    private const string ProbeTitle = "probe";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ApiFactory _factory = null!;
    private string _database = null!;
    private readonly List<HubConnection> _connections = [];

    public async ValueTask InitializeAsync()
    {
        _database = await containers.CreateDatabaseAsync();
        _factory = new ApiFactory(_database, containers.RedisConnectionString);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var connection in _connections)
        {
            await connection.DisposeAsync();
        }

        await _factory.DisposeAsync();
    }

    private sealed record Account(Guid Id, string Token);

    private async Task<Account> SignInAsync()
    {
        using var client = AuthApi.CreateClient(_factory);
        var login = await new AuthApi(_factory, client).SignInWithRoleAsync($"live-{Guid.NewGuid():N}@example.com", "Customer");
        return new Account(SubjectOf(login.AccessToken), login.AccessToken);
    }

    /// <summary>The <c>sub</c> claim of a JWT, read from its payload (the test needs the id the server will see).</summary>
    private static Guid SubjectOf(string jwt)
    {
        var payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
        using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
        return Guid.Parse(document.RootElement.GetProperty("sub").GetString()!);
    }

    private HubConnection Connect(string? token)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, UserHub.Path), options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult(token);
            })
            .Build();
        _connections.Add(connection);
        return connection;
    }

    /// <summary>
    /// Connects a tab and waits until the server has put it in its user's group. The group join runs after the handshake, so a
    /// started client is not yet reachable: the tab probes itself with a marker notification until that arrives (and hides it).
    /// </summary>
    private async Task<BlockingCollection<JsonElement>> ListenAsync(Account account, Action<HubConnection>? configure = null)
    {
        var connection = Connect(account.Token);
        var received = new BlockingCollection<JsonElement>();
        var joined = new TaskCompletionSource();
        connection.On<JsonElement>(UserHub.NotificationMethod, json =>
        {
            if (json.GetProperty("title").GetString() == ProbeTitle)
            {
                joined.TrySetResult();
            }
            else
            {
                received.Add(json);
            }
        });
        configure?.Invoke(connection);
        await connection.StartAsync(Ct);

        var notifier = _factory.Services.GetRequiredService<IUserNotifier>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(Wait);
        while (!joined.Task.IsCompleted)
        {
            await notifier.PushAsync(account.Id, Item(ProbeTitle), Ct);
            await Task.WhenAny(joined.Task, Task.Delay(50, timeout.Token));
            timeout.Token.ThrowIfCancellationRequested();
        }

        return received;
    }

    private static NotificationItem Item(string title = "Đơn hàng đã thanh toán") => new(
        Guid.NewGuid(), "OrderPaid", title, "Cảm ơn bạn đã mua vé.", "/account/orders/1", Read: false,
        new DateTimeOffset(2026, 11, 11, 9, 0, 0, TimeSpan.Zero));

    private static JsonElement Take(BlockingCollection<JsonElement> received) =>
        received.TryTake(out var item, Wait) ? item : throw new ShouldAssertException($"Nothing arrived within {Wait}.");

    [Fact]
    public async Task Connection_without_token_is_rejected()
    {
        var connection = Connect(null);

        await Should.ThrowAsync<HttpRequestException>(() => connection.StartAsync(Ct));
    }

    [Fact]
    public async Task User_receives_own_notification_on_every_tab()
    {
        var user = await SignInAsync();
        var firstTab = await ListenAsync(user);
        var secondTab = await ListenAsync(user);
        var item = Item();

        await _factory.Services.GetRequiredService<IUserNotifier>().PushAsync(user.Id, item, Ct);

        foreach (var tab in new[] { firstTab, secondTab })
        {
            var json = Take(tab);
            json.GetProperty("id").GetGuid().ShouldBe(item.Id);
            json.GetProperty("title").GetString().ShouldBe(item.Title);
        }
    }

    [Fact]
    public async Task The_notification_arrives_as_the_camel_case_item_the_web_client_reads()
    {
        var user = await SignInAsync();
        var tab = await ListenAsync(user);
        var item = Item();

        await _factory.Services.GetRequiredService<IUserNotifier>().PushAsync(user.Id, item, Ct);

        var json = Take(tab);
        json.EnumerateObject().Select(p => p.Name).Order().ShouldBe(["body", "createdAt", "id", "link", "read", "title", "type"]);
        json.GetProperty("id").GetGuid().ShouldBe(item.Id);
        json.GetProperty("type").GetString().ShouldBe("OrderPaid");
        json.GetProperty("body").GetString().ShouldBe(item.Body);
        json.GetProperty("link").GetString().ShouldBe("/account/orders/1");
        json.GetProperty("read").GetBoolean().ShouldBeFalse();
        json.GetProperty("createdAt").GetDateTimeOffset().ShouldBe(item.CreatedAt);
    }

    [Fact]
    public async Task User_never_receives_another_users_notification()
    {
        var owner = await SignInAsync();
        var other = await SignInAsync();
        var ownerTab = await ListenAsync(owner);
        var otherTab = await ListenAsync(other);

        await _factory.Services.GetRequiredService<IUserNotifier>().PushAsync(owner.Id, Item(), Ct);

        Take(ownerTab); // the push was delivered at all, so silence on the other tab means it was filtered
        otherTab.TryTake(out _, Quiet).ShouldBeFalse();
    }

    [Fact]
    public async Task OrderStatusChanged_reaches_the_owner()
    {
        var owner = await SignInAsync();
        var other = await SignInAsync();
        var orderId = Guid.NewGuid();
        var changes = new BlockingCollection<(Guid OrderId, string Status)>();
        var otherChanges = new BlockingCollection<(Guid OrderId, string Status)>();
        await ListenAsync(owner, c => c.On<Guid, string>(UserHub.OrderStatusChangedMethod, (id, status) => changes.Add((id, status))));
        await ListenAsync(other, c => c.On<Guid, string>(UserHub.OrderStatusChangedMethod, (id, status) => otherChanges.Add((id, status))));

        await _factory.Services.GetRequiredService<IUserNotifier>().PushOrderStatusAsync(owner.Id, orderId, "Paid", Ct);

        changes.TryTake(out var change, Wait).ShouldBeTrue();
        change.ShouldBe((orderId, "Paid"));
        otherChanges.TryTake(out _, Quiet).ShouldBeFalse();
    }

    [Fact]
    public async Task A_client_cannot_join_a_group_of_its_own_choosing()
    {
        var attacker = await SignInAsync();
        var victim = await SignInAsync();
        var tab = Connect(attacker.Token);
        await tab.StartAsync(Ct);

        await Should.ThrowAsync<HubException>(() => tab.InvokeAsync("JoinEvent", Guid.NewGuid(), Ct));
        await Should.ThrowAsync<HubException>(() => tab.InvokeAsync("JoinGroup", UserHub.Group(victim.Id), Ct));
    }

    [Fact]
    public async Task Push_from_the_worker_side_reaches_the_api_through_the_backplane()
    {
        // A second, independent host built like the worker: its hub context reaches the api's clients only through Redis.
        await using var worker = new NotificationTestHost(_database, containers.RedisConnectionString, new ScriptedEmailSender(), new FakeTimeProvider(NotificationTestHost.Start));
        var user = await SignInAsync();
        var tab = await ListenAsync(user);
        var item = Item("Từ worker");

        await worker.Services.GetRequiredService<IUserNotifier>().PushAsync(user.Id, item, Ct);

        Take(tab).GetProperty("id").GetGuid().ShouldBe(item.Id);
    }

    [Fact]
    public async Task The_api_and_the_worker_style_host_both_resolve_the_real_notifier()
    {
        await using var worker = new NotificationTestHost(_database, containers.RedisConnectionString, new ScriptedEmailSender(), new FakeTimeProvider(NotificationTestHost.Start));

        _factory.Services.GetRequiredService<IUserNotifier>().ShouldBeOfType<UserNotifier>();
        worker.Services.GetRequiredService<IUserNotifier>().ShouldBeOfType<UserNotifier>();
    }

    [Fact]
    public async Task A_failing_hub_does_not_fail_the_push_and_logs_only_the_exception_type()
    {
        var logs = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var notifier = new UserNotifier(new FakeHubContext(new TimeoutException("redis at 10.0.0.7 is down")), factory.CreateLogger<UserNotifier>());
        var userId = Guid.NewGuid();
        var item = Item("Tiêu đề bí mật");

        await notifier.PushAsync(userId, item, Ct);
        await notifier.PushOrderStatusAsync(userId, Guid.NewGuid(), "Paid", Ct);

        var lines = logs.Lines.ToArray();
        lines.Length.ShouldBe(2);
        lines.ShouldAllBe(line => line.Contains(nameof(TimeoutException)));
        lines.ShouldNotContain(line => line.Contains(userId.ToString()) || line.Contains("bí mật") || line.Contains("10.0.0.7"));
    }

    [Fact]
    public async Task A_cancelled_push_is_rethrown_but_a_failure_while_not_cancelled_is_not()
    {
        var notifier = new UserNotifier(new FakeHubContext(new OperationCanceledException()), NullLogger<UserNotifier>.Instance);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await notifier.PushAsync(Guid.NewGuid(), Item(), CancellationToken.None); // cancellation unrelated to the caller's token: swallowed
        await Should.ThrowAsync<OperationCanceledException>(() => notifier.PushAsync(Guid.NewGuid(), Item(), cancelled.Token));
        await Should.ThrowAsync<OperationCanceledException>(() => notifier.PushOrderStatusAsync(Guid.NewGuid(), Guid.NewGuid(), "Paid", cancelled.Token));
    }

    /// <summary>A hub context whose every send fails with the given exception (the backplane is down).</summary>
    private sealed class FakeHubContext(Exception failure) : IHubContext<UserHub>
    {
        public IHubClients Clients { get; } = new FailingClients(failure);

        public IGroupManager Groups => throw new NotSupportedException();
    }

    private sealed class FailingClients(Exception failure) : IHubClients
    {
        private readonly FailingProxy _proxy = new(failure);

        public IClientProxy All => _proxy;

        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => _proxy;

        public IClientProxy Client(string connectionId) => _proxy;

        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => _proxy;

        public IClientProxy Group(string groupName) => _proxy;

        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => _proxy;

        public IClientProxy Groups(IReadOnlyList<string> groupNames) => _proxy;

        public IClientProxy User(string userId) => _proxy;

        public IClientProxy Users(IReadOnlyList<string> userIds) => _proxy;
    }

    private sealed class FailingProxy(Exception failure) : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) => Task.FromException(failure);
    }
}
