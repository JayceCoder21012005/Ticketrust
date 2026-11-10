using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TicketRush.Api.Common.Auth;
using TicketRush.Api.Common.Http;
using TicketRush.Api.Common.Idempotency;
using TicketRush.Api.IntegrationTests.Holds;
using TicketRush.Api.Features.Queue;
using TicketRush.Application.Abstractions.Notifications;
using TicketRush.Domain.Primitives;
using TicketRush.Infrastructure.Outbox;
using TicketRush.Infrastructure.Persistence;

namespace TicketRush.Api.IntegrationTests.Infrastructure;

/// <summary>Records emails instead of sending them over SMTP.</summary>
public sealed class CapturingEmailSender : IEmailSender
{
    public ConcurrentQueue<EmailMessage> Sent { get; } = new();

    public Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        Sent.Enqueue(message);
        return Task.CompletedTask;
    }
}

/// <summary>The real api pipeline against real containers, plus a few test-only endpoints.</summary>
public sealed class ApiFactory(
    string databaseConnectionString,
    string redisConnectionString,
    string environment = "Production",
    IReadOnlyDictionary<string, string?>? settings = null)
    : WebApplicationFactory<Program>
{
    public CapturingEmailSender Emails { get; } = new();

    public QueryCounter Queries { get; } = new();

    public string StorageRoot { get; } = Path.Combine(Path.GetTempPath(), "ticketrush-tests", Guid.NewGuid().ToString("N"));

    /// <summary>Delivers every pending outbox message, like the worker would (e.g. to send account emails).</summary>
    public async Task DrainOutboxAsync(CancellationToken ct)
    {
        var processor = Services.GetRequiredService<OutboxProcessor>();
        while (await processor.ProcessBatchAsync(ct) > 0)
        {
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        foreach (var (key, value) in TestSettings.For(databaseConnectionString, redisConnectionString))
        {
            builder.UseSetting(key, value);
        }

        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            builder.UseSetting(key, value);
        }

        builder.UseSetting("Storage:LocalRootPath", StorageRoot);
        builder.ConfigureServices(services => services.AddSingleton<IStartupFilter, TestEndpointsStartupFilter>());
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IEmailSender>(Emails);
            services.ConfigureDbContext<AppDbContext>(options => options.AddInterceptors(Queries));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(StorageRoot))
        {
            Directory.Delete(StorageRoot, recursive: true);
        }
    }

    /// <summary>Runs after the app pipeline is built, so these endpoints sit inside the exception handler.</summary>
    private sealed class TestEndpointsStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            app.Map("/_test/throw", branch => branch.Run(_ => throw new InvalidOperationException("secret internal detail")));
            app.Map("/_test/conflict", branch => branch.Run(context =>
                Error.Conflict("seat.already_held", "Seat is already held.").ToProblem().ExecuteAsync(context)));
            app.Map("/_test/auth", branch => WithEndpoints(branch, endpoints =>
            {
                endpoints.MapGet("/user", WhoAmI).RequireAuthorization();
                endpoints.MapGet("/organizer", WhoAmI).RequireAuthorization(AuthPolicies.RequireOrganizer);
                endpoints.MapGet("/admin", WhoAmI).RequireAuthorization(AuthPolicies.RequireAdmin);
                endpoints.MapGet("/gate", WhoAmI).RequireAuthorization(AuthPolicies.RequireGateStaff);
                endpoints.MapGet("/admission/{eventId:guid}", WhoAmI).RequireAuthorization().RequireAdmissionToken();
            }));
            app.Map("/_test/idempotency", branch => WithEndpoints(branch, endpoints =>
                endpoints.MapPost("/", IdempotencyProbe.HandleAsync).RequireAuthorization().RequireIdempotencyKey(TimeSpan.FromMinutes(1))));
            app.Map("/hubs/_test", branch => WithEndpoints(branch, endpoints => endpoints.MapGet("/", WhoAmI).RequireAuthorization()));
        };

        private static string WhoAmI(ClaimsPrincipal user) => user.FindFirstValue("sub") ?? string.Empty;

        private static void WithEndpoints(IApplicationBuilder branch, Action<Microsoft.AspNetCore.Routing.IEndpointRouteBuilder> map)
        {
            branch.UseRouting();
            branch.UseAuthentication();
            branch.UseAuthorization();
            branch.UseEndpoints(map);
        }
    }
}
