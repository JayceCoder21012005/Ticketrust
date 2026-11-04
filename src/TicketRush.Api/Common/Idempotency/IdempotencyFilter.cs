using System.Security.Cryptography;
using TicketRush.Api.Common.Auth;
using TicketRush.Api.Common.Http;
using TicketRush.Application.Abstractions.Idempotency;
using TicketRush.Domain.Primitives;

namespace TicketRush.Api.Common.Idempotency;

/// <summary>
/// <c>Idempotency-Key</c> (spec 00, C2; IETF draft-ietf-httpapi-idempotency-key-header): the first request with a key
/// runs and its response is stored per user; the same request again gets that response back without running; the key
/// with another body is 422; a retry while the first still runs is 409. Server errors are not stored, so retries can
/// succeed. Redis down → 503 <c>system.busy</c>.
/// </summary>
public sealed class IdempotencyFilter(TimeSpan ttl) : IEndpointFilter
{
    public const string HeaderName = "Idempotency-Key";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (!Guid.TryParse(http.Request.Headers[HeaderName].ToString(), out var key))
        {
            return Problem(StatusCodes.Status400BadRequest, "idempotency.key_required", "Send a UUID in the Idempotency-Key header.");
        }

        var store = http.RequestServices.GetRequiredService<IIdempotencyStore>();
        var userId = http.User.UserId();
        try
        {
            var fingerprint = await FingerprintAsync(http.Request);
            return await store.BeginAsync(userId, key, fingerprint, ttl, http.RequestAborted) switch
            {
                IdempotencyBegin.Started => await RunAndStoreAsync(context, next, store, userId, key, fingerprint),
                IdempotencyBegin.Replay replay => new StoredResult(replay.Status, replay.ContentType, replay.Body),
                IdempotencyBegin.InProgress => Problem(StatusCodes.Status409Conflict, "idempotency.in_progress", "A request with this Idempotency-Key is still being processed."),
                _ => Problem(StatusCodes.Status422UnprocessableEntity, "idempotency.key_reused", "This Idempotency-Key was used for a different request."),
            };
        }
        catch (IdempotencyUnavailableException)
        {
            return CommonErrors.SystemBusy.ToProblem();
        }
    }

    private async Task<IResult> RunAndStoreAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next, IIdempotencyStore store, Guid userId, Guid key, string fingerprint)
    {
        var http = context.HttpContext;
        StoredResult captured;
        try
        {
            var result = await next(context);
            captured = await CaptureAsync(http, result as IResult ?? TypedResults.Ok(result));
        }
        catch
        {
            await store.AbandonAsync(userId, key, CancellationToken.None);
            throw;
        }

        if (captured.Status >= 500)
        {
            await store.AbandonAsync(userId, key, CancellationToken.None);
        }
        else
        {
            await store.CompleteAsync(userId, key, fingerprint, captured.Status, captured.ContentType, captured.Body, ttl, CancellationToken.None);
        }

        return captured;
    }

    /// <summary>Runs the endpoint's result into memory so the exact bytes can be stored and replayed.</summary>
    private static async Task<StoredResult> CaptureAsync(HttpContext http, IResult result)
    {
        var original = http.Response.Body;
        await using var buffer = new MemoryStream();
        http.Response.Body = buffer;
        try
        {
            await result.ExecuteAsync(http);
        }
        finally
        {
            http.Response.Body = original;
        }

        return new StoredResult(http.Response.StatusCode, http.Response.ContentType ?? "application/json", buffer.ToArray());
    }

    /// <summary>Method, path and body (buffered by <see cref="IdempotencyExtensions.UseIdempotencyBuffering"/>).</summary>
    private static async Task<string> FingerprintAsync(HttpRequest request)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(System.Text.Encoding.UTF8.GetBytes($"{request.Method} {request.Path}\n"));
        if (request.Body.CanSeek)
        {
            request.Body.Position = 0;
            var bytes = new byte[8192];
            int read;
            while ((read = await request.Body.ReadAsync(bytes)) > 0)
            {
                sha.AppendData(bytes, 0, read);
            }
        }

        return Convert.ToHexString(sha.GetHashAndReset());
    }

    private static Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult Problem(int status, string code, string title) =>
        TypedResults.Problem(statusCode: status, title: title, type: $"https://httpstatuses.io/{status}", extensions: new Dictionary<string, object?> { ["code"] = code });

    private sealed class StoredResult(int status, string contentType, byte[] body) : IResult
    {
        public int Status => status;

        public string ContentType => contentType;

        public byte[] Body => body;

        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = status;
            httpContext.Response.ContentType = contentType;
            return httpContext.Response.Body.WriteAsync(body).AsTask();
        }
    }
}

public static class IdempotencyExtensions
{
    /// <summary>Requires an <c>Idempotency-Key</c>; responses are remembered for <paramref name="ttl"/> (default 24 h).</summary>
    public static TBuilder RequireIdempotencyKey<TBuilder>(this TBuilder builder, TimeSpan? ttl = null)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(new IdempotencyFilter(ttl ?? TimeSpan.FromHours(24)));

    /// <summary>Keeps request bodies readable after model binding when they carry an Idempotency-Key (for the fingerprint).</summary>
    public static IApplicationBuilder UseIdempotencyBuffering(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            if (context.Request.Headers.ContainsKey(IdempotencyFilter.HeaderName))
            {
                context.Request.EnableBuffering();
            }

            return next(context);
        });
}
