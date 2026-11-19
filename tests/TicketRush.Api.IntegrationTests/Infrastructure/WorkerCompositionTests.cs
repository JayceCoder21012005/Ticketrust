using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TicketRush.Application;
using TicketRush.Infrastructure;
using TicketRush.Infrastructure.Realtime;

namespace TicketRush.Api.IntegrationTests.Infrastructure;

/// <summary>
/// The worker registers the application handlers too, and a Development host checks every registration when it is built. A
/// handler needing a service only the api registers took the worker down at start in Aspire (and so the mock webhooks were never
/// delivered) while every Production host and every api test was fine.
/// </summary>
public sealed class WorkerCompositionTests
{
    [Fact]
    public void Every_service_the_worker_registers_can_be_built_in_development()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Configuration.AddInMemoryCollection(TestSettings.For("Host=localhost;Database=unused", "localhost:1,abortConnect=false"));
        builder.AddInfrastructure();
        builder.Services.AddApplication();
        builder.Services.AddTicketRushSignalR(builder.Configuration);

        using var host = builder.Build(); // ValidateOnBuild: throws on any registration that cannot be resolved

        host.Services.ShouldNotBeNull();
    }
}
