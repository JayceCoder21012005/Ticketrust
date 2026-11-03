using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.FileProviders;
using TicketRush.Infrastructure.Storage;

namespace TicketRush.Api.Features.Storage;

/// <summary>
/// The Local storage provider's side of presigned uploads: <c>PUT</c> with the token from the upload URL,
/// and files served read-only under <c>/media</c> (proxied by the web app, like <c>/api</c>).
/// </summary>
public static class LocalStorageEndpoints
{
    public static RouteGroupBuilder MapLocalStorageEndpoints(this RouteGroupBuilder v1)
    {
        v1.MapPut("/storage/uploads", UploadAsync)
            .WithTags("Storage")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge);
        return v1;
    }

    public static WebApplication UseLocalMedia(this WebApplication app)
    {
        var root = app.Services.GetRequiredService<LocalFileStorage>().RootPath;
        Directory.CreateDirectory(root);
        app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(root), RequestPath = "/media" });
        return app;
    }

    private static async Task<IResult> UploadAsync(string? token, HttpRequest request, LocalFileStorage storage, CancellationToken ct)
    {
        if (storage.ReadGrant(token) is not { } grant)
        {
            return Problem(StatusCodes.Status403Forbidden, "storage.upload_forbidden", "The upload link is invalid or has expired.");
        }

        if (!string.Equals(request.ContentType, grant.ContentType, StringComparison.OrdinalIgnoreCase))
        {
            return Problem(StatusCodes.Status400BadRequest, "storage.content_type_mismatch", $"Upload must be {grant.ContentType}.");
        }

        if (request.ContentLength is not { } length || length > grant.MaxBytes)
        {
            return Problem(StatusCodes.Status413PayloadTooLarge, "storage.too_large", "The file is larger than allowed.");
        }

        await storage.SaveAsync(grant.Key, request.Body, ct);
        return TypedResults.NoContent();
    }

    private static ProblemHttpResult Problem(int status, string code, string title) =>
        TypedResults.Problem(statusCode: status, title: title, extensions: new Dictionary<string, object?> { ["code"] = code });
}
