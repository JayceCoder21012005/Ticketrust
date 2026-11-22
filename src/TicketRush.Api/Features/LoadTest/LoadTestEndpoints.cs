using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using TicketRush.Infrastructure.LoadTest;

namespace TicketRush.Api.Features.LoadTest;

/// <summary>
/// <c>POST /api/v1/loadtest/seed</c> (spec 17): fixtures for the k6 scenarios. It exists only in the <c>LoadTest</c> environment
/// (any other host never maps it, so it answers 404 like any unknown path), and even there only for a caller that knows the
/// <c>LoadTest:Key</c> secret. It is not in the OpenAPI document.
/// </summary>
public static class LoadTestEndpoints
{
    public const string KeyHeader = "X-LoadTest-Key";

    public static IServiceCollection AddLoadTestFixtures(this IServiceCollection services, IHostEnvironment environment)
    {
        if (environment.IsEnvironment(LoadTestOptions.EnvironmentName))
        {
            services.AddOptions<LoadTestOptions>().BindConfiguration(LoadTestOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
            services.AddScoped<LoadTestSeeder>();
        }

        return services;
    }

    public static RouteGroupBuilder MapLoadTestEndpoints(this RouteGroupBuilder v1, IHostEnvironment environment)
    {
        if (!environment.IsEnvironment(LoadTestOptions.EnvironmentName))
        {
            return v1;
        }

        v1.MapPost("/loadtest/seed", SeedAsync).ExcludeFromDescription();
        return v1;
    }

    private static async Task<IResult> SeedAsync(
        HttpRequest http, LoadTestSeedRequest? request, IOptions<LoadTestOptions> options, LoadTestSeeder seeder, CancellationToken ct)
    {
        // A wrong key looks like no endpoint at all.
        if (!KeyMatches(http.Headers[KeyHeader].ToString(), options.Value.Key))
        {
            return TypedResults.NotFound();
        }

        request ??= new LoadTestSeedRequest();
        if (request.Problem() is { } problem)
        {
            return TypedResults.Problem(problem, statusCode: StatusCodes.Status400BadRequest);
        }

        return TypedResults.Ok(await seeder.SeedAsync(request, ct));
    }

    private static bool KeyMatches(string given, string expected) =>
        given.Length > 0 && CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(given)), SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
}
