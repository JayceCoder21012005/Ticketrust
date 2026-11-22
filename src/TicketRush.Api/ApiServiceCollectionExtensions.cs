using System.Text.Json.Serialization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using TicketRush.Api.Common.Auth;
using TicketRush.Api.Common.Idempotency;
using TicketRush.Api.Common.Errors;
using TicketRush.Api.Common.Middleware;
using TicketRush.Api.Common.RateLimiting;
using TicketRush.Api.Features.Auth;
using TicketRush.Api.Features.Catalog;
using TicketRush.Api.Features.Organizer;
using TicketRush.Api.Features.Holds;
using TicketRush.Api.Features.Follows;
using TicketRush.Api.Features.Gate;
using TicketRush.Api.Features.Notifications;
using TicketRush.Api.Features.Orders;
using TicketRush.Api.Features.Payments;
using TicketRush.Api.Features.Queue;
using TicketRush.Api.Features.ServerTime;
using TicketRush.Api.Features.Storage;
using TicketRush.Api.Features.Tickets;
using TicketRush.Api.Features.Dashboard;
using TicketRush.Api.Features.Resale;
using TicketRush.Api.Features.Waitlist;
using TicketRush.Api.Features.Users;
using TicketRush.Api.Features.Webhooks;
using TicketRush.Infrastructure.Realtime;
using TicketRush.Application.Options;
using TicketRush.Infrastructure;

namespace TicketRush.Api;

public static class ApiServiceCollectionExtensions
{
    public const string WebCorsPolicy = "web";

    public static IServiceCollection AddApi(this IServiceCollection services)
    {
        services.AddProblemDetails(options => options.CustomizeProblemDetails = ProblemDetailsCustomizer.Customize);
        services.AddExceptionHandler<GlobalExceptionHandler>();
        services.AddOpenApi();
        services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        services.AddCors();
        services.AddOptions<Microsoft.AspNetCore.Cors.Infrastructure.CorsOptions>()
            .Configure<IOptions<FrontendOptions>>((cors, frontend) => cors.AddPolicy(WebCorsPolicy, policy => policy
                .WithOrigins(frontend.Value.BaseUrl.TrimEnd('/'))
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials()));

        services.AddOptions<ForwardedHeadersOptions>().Configure<IConfiguration>((options, configuration) =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
            // Spec 12: per-ip rate limits are only as good as the client ip. With ForwardedHeaders:KnownNetworks set (the
            // reverse proxy's subnet, CIDR) X-Forwarded-For is trusted from those hops only. Unset, every hop is trusted:
            // fine while the api is reachable only through the proxy on a private network (docker-compose.prod.yml).
            foreach (var cidr in configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? [])
            {
                options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(cidr));
            }
        });

        services.AddApiAuthentication();
        services.AddApiRateLimiting();
        services.AddCaptcha();
        services.AddSingleton<PaymentWebhookMetrics>();
        services.AddScoped<PaymentWebhookReceiver>();
        return services;
    }

    public static WebApplication UseApiPipeline(this WebApplication app)
    {
        app.UseForwardedHeaders();
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseMiddleware<RequestLoggingMiddleware>();
        app.UseExceptionHandler();
        app.UseStatusCodePages();

        if (!app.Environment.IsDevelopment())
        {
            app.UseHsts();
        }

        app.UseMiddleware<SecurityHeadersMiddleware>();
        app.UseCors(WebCorsPolicy);
        app.UseLocalMedia();
        app.UseAuthentication();
        // After authentication (the per-user policies need the user), before authorization (refused requests count too).
        app.UseRateLimiter();
        app.UseAuthorization();
        app.UseIdempotencyBuffering();
        return app;
    }

    public static WebApplication MapApiEndpoints(this WebApplication app)
    {
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }

        var v1 = app.MapGroup("/api/v1");
        v1.MapServerTimeEndpoints();
        v1.MapAuthEndpoints();
        v1.MapMeEndpoints();
        v1.MapCatalogEndpoints();
        v1.MapSearchEndpoints();
        v1.MapOrganizerEndpoints();
        v1.MapQueueEndpoints();
        v1.MapHoldEndpoints();
        v1.MapOrderEndpoints();
        v1.MapNotificationEndpoints();
        v1.MapTicketEndpoints();
        v1.MapGateEndpoints();
        v1.MapFollowEndpoints();
        v1.MapWaitlistEndpoints();
        v1.MapResaleEndpoints();
        v1.MapDashboardEndpoints();
        v1.MapMockGatewayEndpoints();
        v1.MapPaymentWebhookEndpoints();
        v1.MapLocalStorageEndpoints();

        app.MapHub<EventsHub>(EventsHub.Path);
        app.MapHub<UserHub>(UserHub.Path);
        app.MapHub<OrganizerHub>(OrganizerHub.Path);
        return app;
    }
}
