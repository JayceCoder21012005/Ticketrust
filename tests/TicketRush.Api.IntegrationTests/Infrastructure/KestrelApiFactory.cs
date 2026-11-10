using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace TicketRush.Api.IntegrationTests.Infrastructure;

/// <summary>
/// The api on a real Kestrel socket (all interfaces, free port) instead of the in-memory TestServer, so a container on the
/// Docker host can reach it. Only used where the wire matters (proxy tests); everything else stays on <see cref="ApiFactory"/>.
/// </summary>
public sealed class KestrelApiFactory(string databaseConnectionString, string redisConnectionString) : WebApplicationFactory<Program>
{
    private IHost? _kestrelHost;

    /// <summary>Services of the Kestrel-hosted app (the one the proxy talks to).</summary>
    public IServiceProvider KestrelServices => _kestrelHost!.Services;

    public int Port => new Uri(_kestrelHost!.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()).Port;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        foreach (var (key, value) in TestSettings.For(databaseConnectionString, redisConnectionString))
        {
            builder.UseSetting(key, value);
        }

        builder.UseSetting("Storage:LocalRootPath", Path.Combine(Path.GetTempPath(), "ticketrush-tests", Guid.NewGuid().ToString("N")));
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // The base class insists on a TestServer host; build that one untouched, plus the real one next to it.
        var testHost = builder.Build();
        builder.ConfigureWebHost(web => web.UseKestrel(kestrel => kestrel.Listen(IPAddress.Any, 0)));
        _kestrelHost = builder.Build();
        _kestrelHost.Start();
        return new CompositeHost(testHost);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _kestrelHost?.Dispose();
        }

        base.Dispose(disposing);
    }

    private sealed class CompositeHost(IHost testHost) : IHost
    {
        public IServiceProvider Services => testHost.Services;

        public void Start() => testHost.Start();

        public Task StartAsync(CancellationToken cancellationToken = default) => testHost.StartAsync(cancellationToken);

        public Task StopAsync(CancellationToken cancellationToken = default) => testHost.StopAsync(cancellationToken);

        public void Dispose() => testHost.Dispose();
    }
}
