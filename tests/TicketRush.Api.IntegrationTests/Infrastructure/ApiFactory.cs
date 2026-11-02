using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using TicketRush.Api.Common.Http;
using TicketRush.Domain.Primitives;

namespace TicketRush.Api.IntegrationTests.Infrastructure;

/// <summary>The real api pipeline against real containers, plus a few test-only endpoints.</summary>
public sealed class ApiFactory(string databaseConnectionString, string redisConnectionString, string environment = "Production")
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        foreach (var (key, value) in TestSettings.For(databaseConnectionString, redisConnectionString))
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureServices(services => services.AddSingleton<IStartupFilter, TestEndpointsStartupFilter>());
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
        };
    }
}
