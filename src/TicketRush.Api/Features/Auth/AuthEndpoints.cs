using FluentValidation;
using Microsoft.AspNetCore.Http.HttpResults;
using TicketRush.Api.Common.Http;
using TicketRush.Application.Abstractions.Identity;
using TicketRush.Application.Features.Auth;
using TicketRush.Application.Features.Auth.ConfirmEmail;
using TicketRush.Application.Features.Auth.ForgotPassword;
using TicketRush.Application.Features.Auth.Login;
using TicketRush.Application.Features.Auth.Logout;
using TicketRush.Application.Features.Auth.Refresh;
using TicketRush.Application.Features.Auth.Register;
using TicketRush.Application.Features.Auth.ResetPassword;
using TicketRush.Domain.Users;

namespace TicketRush.Api.Features.Auth;

/// <summary>The refresh token only ever travels in the cookie; the body carries the access token.</summary>
public sealed record LoginResponse(string AccessToken, DateTimeOffset ExpiresAt, UserAccount User);

public static class AuthEndpoints
{
    public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder v1)
    {
        var auth = v1.MapGroup("/auth").WithTags("Auth");

        auth.MapPost("/register", RegisterAsync).Produces(StatusCodes.Status202Accepted).ProducesValidationProblem();
        auth.MapPost("/confirm-email", ConfirmEmailAsync).Produces(StatusCodes.Status204NoContent).ProducesValidationProblem();
        auth.MapPost("/login", LoginAsync).Produces<LoginResponse>().ProducesProblem(StatusCodes.Status401Unauthorized);
        auth.MapPost("/refresh", RefreshAsync).AddEndpointFilter(RequireFetchHeader)
            .Produces<LoginResponse>().ProducesProblem(StatusCodes.Status401Unauthorized);
        auth.MapPost("/logout", LogoutAsync).AddEndpointFilter(RequireFetchHeader).Produces(StatusCodes.Status204NoContent);
        auth.MapPost("/forgot-password", ForgotPasswordAsync).Produces(StatusCodes.Status202Accepted).ProducesValidationProblem();
        auth.MapPost("/reset-password", ResetPasswordAsync).Produces(StatusCodes.Status204NoContent).ProducesValidationProblem();
        return v1;
    }

    private static async Task<IResult> RegisterAsync(
        RegisterCommand command, IValidator<RegisterCommand> validator, RegisterHandler handler, CancellationToken ct)
    {
        if (await validator.ValidateToProblemAsync(command, ct) is { } invalid)
        {
            return invalid;
        }

        var result = await handler.HandleAsync(command, ct);
        return result.IsSuccess ? TypedResults.Accepted((string?)null) : result.Error.ToProblem();
    }

    private static async Task<IResult> ConfirmEmailAsync(
        ConfirmEmailCommand command, IValidator<ConfirmEmailCommand> validator, ConfirmEmailHandler handler, CancellationToken ct) =>
        await validator.ValidateToProblemAsync(command, ct) ?? (await handler.HandleAsync(command, ct)).ToHttpResult();

    private static async Task<IResult> LoginAsync(
        LoginCommand command, IValidator<LoginCommand> validator, LoginHandler handler, HttpContext http, CancellationToken ct)
    {
        if (await validator.ValidateToProblemAsync(command, ct) is { } invalid)
        {
            return invalid;
        }

        var result = await handler.HandleAsync(command, ClientOf(http), ct);
        return result.ToHttpResult(session => SignedIn(http, session));
    }

    private static async Task<IResult> RefreshAsync(RefreshHandler handler, HttpContext http, CancellationToken ct)
    {
        var result = await handler.HandleAsync(http.Request.Cookies[AuthCookies.Refresh], ClientOf(http), ct);
        if (result.IsFailure)
        {
            AuthCookies.Clear(http.Response);
        }

        return result.ToHttpResult(session => SignedIn(http, session));
    }

    private static async Task<IResult> LogoutAsync(LogoutHandler handler, HttpContext http, CancellationToken ct)
    {
        await handler.HandleAsync(http.Request.Cookies[AuthCookies.Refresh], ct);
        AuthCookies.Clear(http.Response);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> ForgotPasswordAsync(
        ForgotPasswordCommand command, IValidator<ForgotPasswordCommand> validator, ForgotPasswordHandler handler, CancellationToken ct)
    {
        if (await validator.ValidateToProblemAsync(command, ct) is { } invalid)
        {
            return invalid;
        }

        await handler.HandleAsync(command, ct);
        return TypedResults.Accepted((string?)null);
    }

    private static async Task<IResult> ResetPasswordAsync(
        ResetPasswordCommand command, IValidator<ResetPasswordCommand> validator, ResetPasswordHandler handler, CancellationToken ct) =>
        await validator.ValidateToProblemAsync(command, ct) ?? (await handler.HandleAsync(command, ct)).ToHttpResult();

    private static Ok<LoginResponse> SignedIn(HttpContext http, AuthSession session)
    {
        AuthCookies.Append(http.Response, session);
        return TypedResults.Ok(new LoginResponse(session.AccessToken.Token, session.AccessToken.ExpiresAt, session.User));
    }

    private static ClientInfo ClientOf(HttpContext http)
    {
        var userAgent = http.Request.Headers.UserAgent.ToString();
        return new ClientInfo(http.Connection.RemoteIpAddress?.ToString(), userAgent.Length > 512 ? userAgent[..512] : userAgent);
    }

    /// <summary>
    /// CSRF defence in depth on cookie-authenticated endpoints: a cross-site form can't set this header,
    /// and a cross-site fetch that sets it needs a CORS preflight that our policy rejects.
    /// </summary>
    private static async ValueTask<object?> RequireFetchHeader(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        context.HttpContext.Request.Headers["X-Requested-With"] == "fetch"
            ? await next(context)
            : AuthErrors.MissingCsrfHeader.ToProblem();
}
