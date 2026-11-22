using TicketRush.Api.Common.Middleware;
using TicketRush.Application.Abstractions.Audit;
using TicketRush.Infrastructure.Identity;

namespace TicketRush.Api.Common.Audit;

/// <summary>
/// The audit actor of an API request (spec 16): the signed-in user (<c>sub</c>), the client IP after the forwarded-headers middleware,
/// and the request's correlation id. Outside a request (startup, hosted services) everything is null, like the system.
/// </summary>
public sealed class HttpAuditContext(IHttpContextAccessor accessor) : IAuditContext
{
    public Guid? ActorUserId =>
        Guid.TryParse(accessor.HttpContext?.User.FindFirst(AuthClaims.Subject)?.Value, out var id) ? id : null;

    public string? ActorIp => accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();

    public string? CorrelationId =>
        accessor.HttpContext is { } http && CorrelationIdMiddleware.TryGet(http, out var correlationId) ? correlationId : null;
}
