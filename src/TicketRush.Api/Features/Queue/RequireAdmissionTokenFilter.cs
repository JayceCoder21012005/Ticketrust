using TicketRush.Api.Common.Auth;
using TicketRush.Api.Common.Http;
using TicketRush.Application.Abstractions.Queue;
using TicketRush.Application.Features.Queue;
using TicketRush.Domain.Primitives;

namespace TicketRush.Api.Features.Queue;

/// <summary>
/// Guards every buy path (spec 07, 08). The caller must present the admission token the waiting room gave them in
/// <c>X-Admission-Token</c>. It is valid only when its signature checks out, it has not expired, it was issued to the
/// authenticated user for the event in the route, and it is still the user's current admission, so leaving the queue
/// revokes it and a copied token is useless to anyone else (C7).
/// </summary>
public sealed class RequireAdmissionTokenFilter : IEndpointFilter
{
    public const string HeaderName = "X-Admission-Token";

    /// <summary><c>HttpContext.Items</c> key of the validated <see cref="AdmissionTokenClaims"/>.</summary>
    public const string ClaimsItemKey = "tr.admission";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (!http.Request.Headers.TryGetValue(HeaderName, out var header) || string.IsNullOrWhiteSpace(header))
        {
            return QueueErrors.AdmissionRequired.ToProblem();
        }

        var token = header.ToString();
        var claims = http.RequestServices.GetRequiredService<IAdmissionTokenFactory>().Validate(token);
        if (claims is null || !Guid.TryParse(RouteEventId(http), out var routeEventId))
        {
            return QueueErrors.AdmissionInvalid.ToProblem();
        }

        if (claims.UserId != http.User.UserId() || claims.EventId != routeEventId)
        {
            return QueueErrors.AdmissionInvalid.ToProblem();
        }

        try
        {
            var room = http.RequestServices.GetRequiredService<IWaitingRoom>();
            if (!await room.IsCurrentTokenAsync(claims.PhaseId, claims.UserId, token, http.RequestAborted))
            {
                return QueueErrors.AdmissionInvalid.ToProblem();
            }
        }
        catch (QueueUnavailableException)
        {
            return CommonErrors.SystemBusy.ToProblem();
        }

        http.Items[ClaimsItemKey] = claims;
        return await next(context);
    }

    private static string? RouteEventId(HttpContext http) =>
        (http.Request.RouteValues.TryGetValue("eventId", out var eventId) ? eventId : http.Request.RouteValues.GetValueOrDefault("id"))?.ToString();
}

public static class RequireAdmissionTokenExtensions
{
    /// <summary>The admission validated by <see cref="RequireAdmissionTokenFilter"/> for this request.</summary>
    public static AdmissionTokenClaims AdmissionClaims(this HttpContext http) =>
        http.Items[RequireAdmissionTokenFilter.ClaimsItemKey] as AdmissionTokenClaims
        ?? throw new InvalidOperationException("RequireAdmissionToken must run before reading the admission.");

    /// <summary>Requires a valid admission token for the event in the route (<c>{eventId}</c> or <c>{id}</c>).</summary>
    public static TBuilder RequireAdmissionToken<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter<TBuilder, RequireAdmissionTokenFilter>();
}
