using Microsoft.AspNetCore.Http;

namespace TicketRush.Api.IntegrationTests.Holds;

/// <summary>Test-only endpoint behind <c>RequireIdempotencyKey</c>: counts runs, can be held open or made to fail.</summary>
public static class IdempotencyProbe
{
    private static int _runs;

    public static int Runs => Volatile.Read(ref _runs);

    /// <summary>When set, the next run waits for it before answering.</summary>
    public static TaskCompletionSource? Gate { get; set; }

    /// <summary>When set, runs answer 503 (a server-side failure that must not be stored).</summary>
    public static bool Fail { get; set; }

    public sealed record Body(string Value);

    public static void Reset()
    {
        Interlocked.Exchange(ref _runs, 0);
        Gate = null;
        Fail = false;
    }

    public static async Task<IResult> HandleAsync(Body body)
    {
        Interlocked.Increment(ref _runs);
        if (Gate is { } gate)
        {
            await gate.Task;
        }

        return Fail
            ? TypedResults.Problem(statusCode: StatusCodes.Status503ServiceUnavailable)
            : TypedResults.Ok(new { id = Guid.NewGuid(), echo = body.Value });
    }
}
