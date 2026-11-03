using System.Security.Claims;
using FluentValidation;
using TicketRush.Api.Common.Auth;
using TicketRush.Api.Common.Http;
using TicketRush.Application.Abstractions.Identity;
using TicketRush.Application.Abstractions.Storage;
using TicketRush.Application.Features.Users.AvatarUpload;
using TicketRush.Application.Features.Users.GetProfile;
using TicketRush.Application.Features.Users.UpdateProfile;

namespace TicketRush.Api.Features.Users;

public sealed record UpdateProfileRequest(string FullName, string? PhoneNumber, string? AvatarUrl);

public sealed record AvatarUploadRequest(string ContentType, long Size);

public static class MeEndpoints
{
    public static RouteGroupBuilder MapMeEndpoints(this RouteGroupBuilder v1)
    {
        var me = v1.MapGroup("/me").WithTags("Account").RequireAuthorization();

        me.MapGet("/", GetAsync).Produces<UserAccount>();
        me.MapPut("/", UpdateAsync).Produces<UserAccount>().ProducesValidationProblem();
        me.MapPost("/avatar-upload-url", CreateAvatarUploadUrlAsync).Produces<UploadTicket>().ProducesValidationProblem();
        return v1;
    }

    private static async Task<IResult> GetAsync(ClaimsPrincipal user, GetProfileHandler handler, CancellationToken ct) =>
        (await handler.HandleAsync(user.UserId(), ct)).ToHttpResult();

    private static async Task<IResult> UpdateAsync(
        UpdateProfileRequest request,
        ClaimsPrincipal user,
        IValidator<UpdateProfileCommand> validator,
        UpdateProfileHandler handler,
        CancellationToken ct)
    {
        var command = new UpdateProfileCommand(user.UserId(), request.FullName, request.PhoneNumber, request.AvatarUrl);
        return await validator.ValidateToProblemAsync(command, ct) ?? (await handler.HandleAsync(command, ct)).ToHttpResult();
    }

    private static async Task<IResult> CreateAvatarUploadUrlAsync(
        AvatarUploadRequest request,
        ClaimsPrincipal user,
        IValidator<AvatarUploadCommand> validator,
        AvatarUploadHandler handler,
        CancellationToken ct)
    {
        var command = new AvatarUploadCommand(user.UserId(), request.ContentType, request.Size);
        return await validator.ValidateToProblemAsync(command, ct) ?? TypedResults.Ok(handler.Handle(command));
    }
}
