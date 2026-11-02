namespace TicketRush.Api.Features.ServerTime;

public static class ServerTimeEndpoints
{
    public static RouteGroupBuilder MapServerTimeEndpoints(this RouteGroupBuilder group)
    {
        // Clients compute their clock offset from this (C10).
        group.MapGet("/time", (TimeProvider time) => TypedResults.Ok(new ServerTimeResponse(time.GetUtcNow())))
            .WithName("GetServerTime")
            .WithTags("ServerTime");

        return group;
    }
}

public sealed record ServerTimeResponse(DateTimeOffset ServerNow);
