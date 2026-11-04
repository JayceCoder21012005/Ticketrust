using System.Text.Json.Serialization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using TicketRush.Api.Common.Auth;
using TicketRush.Api.Common.Idempotency;
using TicketRush.Api.Common.Errors;
using TicketRush.Api.Common.Middleware;
using TicketRush.Api.Features.Auth;
using TicketRush.Api.Features.Catalog;
using TicketRush.Api.Features.Organizer;
using TicketRush.Api.Features.Queue;
using TicketRush.Api.Features.ServerTime;
using TicketRush.Api.Features.Storage;
using TicketRush.Api.Features.Users;
using TicketRush.Application.Options;

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

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            // The reverse proxy / Next.js runs on a private network in every deployment we support.
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
        });

        services.AddApiAuthentication();
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
        v1.MapOrganizerEndpoints();
        v1.MapQueueEndpoints();
        v1.MapLocalStorageEndpoints();
        return app;
    }
}
