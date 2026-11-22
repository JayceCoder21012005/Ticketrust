using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Application.Abstractions.Catalog;

namespace TicketRush.Api.IntegrationTests.Catalog;

/// <summary>
/// Found by the spec 17 load test: the outbox handles its messages one after another, so a revalidation call to a web host that
/// does not answer held every message behind it (inventory commits, emails, refunds) for the HTTP client's retries and its
/// 30 s budget. The outbox retries a failed message with its own backoff, so the call gets one short attempt and no retries.
/// </summary>
public sealed class WebCacheRevalidatorTimeoutTests(ContainersFixture containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_web_host_that_never_answers_fails_the_call_quickly_and_is_tried_once()
    {
        // Accepts connections and never answers.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var connections = 0;
        using var stop = new CancellationTokenSource();
        var accepting = Task.Run(
            async () =>
        {
            var held = new List<TcpClient>();
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    held.Add(await listener.AcceptTcpClientAsync(stop.Token));
                    Interlocked.Increment(ref connections);
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                held.ForEach(c => c.Dispose());
            }
        },
            Ct);

        await using var factory = new ApiFactory(await containers.CreateDatabaseAsync(), containers.RedisConnectionString, settings: new Dictionary<string, string?>
        {
            ["Frontend:RevalidateUrl"] = $"http://127.0.0.1:{port}/api/revalidate",
            ["Frontend:RevalidateSecret"] = "test-revalidate-secret-0123456789",
        });
        await using var scope = factory.Services.CreateAsyncScope();
        var revalidator = scope.ServiceProvider.GetRequiredService<IWebCacheRevalidator>();

        var watch = Stopwatch.StartNew();
        await Should.ThrowAsync<Exception>(() => revalidator.RevalidateAsync(["events:list"], Ct));
        watch.Stop();

        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(8));
        connections.ShouldBe(1, "the outbox retries the message; the HTTP client must not retry on top");
        await stop.CancelAsync();
        listener.Stop();
        await accepting;
    }
}
