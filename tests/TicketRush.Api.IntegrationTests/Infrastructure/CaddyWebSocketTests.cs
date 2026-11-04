using System.Collections.Concurrent;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using TicketRush.Api.IntegrationTests.Auth;
using TicketRush.Infrastructure.Realtime;

namespace TicketRush.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Task A4 (spec 07/08): the production Caddyfile, unchanged except for the site address and the two upstreams, in front of the
/// api on a real socket. A real SignalR client must get WebSocket through it (no long-polling fallback) and receive a SeatDelta.
/// Needs Docker (like every test in this assembly) and the <c>caddy:2</c> image.
/// </summary>
public sealed class CaddyWebSocketTests(ContainersFixture containers) : IAsyncLifetime
{
    private const string HostAlias = "host.docker.internal";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private KestrelApiFactory _api = null!;
    private ApiFactory _auth = null!;
    private string _token = null!;
    private IContainer? _caddy;

    public async ValueTask InitializeAsync()
    {
        var database = await containers.CreateDatabaseAsync();
        _api = new KestrelApiFactory(database, containers.RedisConnectionString);
        _ = _api.Services; // forces both hosts to start
        _auth = new ApiFactory(database, containers.RedisConnectionString);
        using var client = AuthApi.CreateClient(_auth);
        _token = (await new AuthApi(_auth, client).SignInWithRoleAsync($"ws-{Guid.NewGuid():N}@example.com", "Customer")).AccessToken;
    }

    public async ValueTask DisposeAsync()
    {
        if (_caddy is not null)
        {
            await _caddy.DisposeAsync();
        }

        await _auth.DisposeAsync();
        await _api.DisposeAsync();
    }

    /// <summary>The real deploy/Caddyfile; only the site address (env) and the upstream hosts differ.</summary>
    private static string TestCaddyfile(int apiPort)
    {
        var text = DeployFiles.Caddyfile();
        text.ShouldContain("reverse_proxy api:8080");
        return text.Replace("reverse_proxy api:8080", $"reverse_proxy {HostAlias}:{apiPort}", StringComparison.Ordinal);
    }

    private async Task<int> StartCaddyAsync(string caddyfile)
    {
        _caddy = new ContainerBuilder("caddy:2")
            .WithExtraHost(HostAlias, "host-gateway")
            .WithEnvironment("PUBLIC_HOST", ":80") // plain HTTP, no certificates; web:3000 stays unresolvable on purpose
            .WithResourceMapping(System.Text.Encoding.UTF8.GetBytes(caddyfile), "/etc/caddy/Caddyfile")
            .WithPortBinding(80, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("serving initial configuration"))
            .Build();
        await _caddy.StartAsync(Ct);
        return _caddy.GetMappedPublicPort(80);
    }

    private HubConnection Connect(int caddyPort, Action<ClientWebSocket> onSocket) => new HubConnectionBuilder()
        .WithUrl($"http://localhost:{caddyPort}/hubs/events", options =>
        {
            options.Transports = HttpTransportType.WebSockets; // no fallback: a failed upgrade fails StartAsync
            options.SkipNegotiation = false;
            options.AccessTokenProvider = () => Task.FromResult<string?>(_token);
            options.WebSocketFactory = async (context, ct) =>
            {
                var socket = new ClientWebSocket();
                socket.Options.CollectHttpResponseDetails = true;
                // What a browser does: it cannot set headers on a WebSocket, so the token travels as access_token.
                var uri = new Uri($"{context.Uri}&access_token={Uri.EscapeDataString(_token)}");
                await socket.ConnectAsync(uri, ct);
                onSocket(socket);
                return socket;
            };
        })
        .Build();

    [Fact]
    public async Task A_seat_delta_reaches_a_websocket_client_through_caddy()
    {
        var eventId = Guid.NewGuid();
        var caddyPort = await StartCaddyAsync(TestCaddyfile(_api.Port));
        ClientWebSocket? socket = null;
        await using var connection = Connect(caddyPort, s => socket = s);
        var deltas = new BlockingCollection<SeatDelta>();
        connection.On<SeatDelta>(EventsHub.SeatDeltaMethod, deltas.Add);

        await connection.StartAsync(Ct);
        await connection.InvokeAsync("JoinEvent", eventId, Ct);

        socket.ShouldNotBeNull();
        socket.State.ShouldBe(WebSocketState.Open);
        socket.HttpStatusCode.ShouldBe(System.Net.HttpStatusCode.SwitchingProtocols);
        // Set by Caddy's `header` block, never by the api: proves the upgrade went through the proxy.
        socket.HttpResponseHeaders!.ShouldContainKey("Strict-Transport-Security");

        var hub = _api.KestrelServices.GetRequiredService<IHubContext<EventsHub>>();
        var sent = new SeatDelta(1, 2, [5, 6], [], [], []);
        await hub.Clients.Group(EventsHub.Group(eventId)).SendAsync(EventsHub.SeatDeltaMethod, sent, Ct);

        deltas.TryTake(out var received, TimeSpan.FromSeconds(5)).ShouldBeTrue("no SeatDelta arrived through Caddy");
        received.ShouldNotBeNull();
        received.Held.ShouldBe([5, 6]);
        received.V.ShouldBe(2);
    }

    [Fact]
    public async Task Without_the_hubs_handle_the_upgrade_goes_to_web_and_fails()
    {
        // Same Caddyfile minus the /hubs/* handle: proves the test above depends on that handle.
        var text = TestCaddyfile(_api.Port);
        var start = text.IndexOf("handle /hubs/*", StringComparison.Ordinal);
        var end = text.IndexOf('}', start) + 1;
        var caddyPort = await StartCaddyAsync(text.Remove(start, end - start));
        await using var connection = Connect(caddyPort, _ => { });

        await Should.ThrowAsync<HttpRequestException>(() => connection.StartAsync(Ct));
    }
}
