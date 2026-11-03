using System.Globalization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TicketRush.Application.Abstractions.Storage;
using TicketRush.Application.Options;
using TicketRush.Infrastructure.Options;

namespace TicketRush.Infrastructure.Storage;

/// <summary>What a presigned upload URL allows: one key, one content type, a maximum size.</summary>
public sealed record UploadGrant(string Key, string ContentType, long MaxBytes);

/// <summary>
/// Disk storage behind the api (<c>Storage:Provider = Local</c>). Upload URLs carry a time-limited
/// Data Protection token instead of an S3 signature; files are served from <c>/media</c>.
/// </summary>
public sealed class LocalFileStorage(
    IOptions<StorageOptions> storage,
    IOptions<FrontendOptions> frontend,
    IDataProtectionProvider dataProtection,
    IHostEnvironment environment,
    TimeProvider clock) : IFileStorage
{
    public const string UploadPath = "/api/v1/storage/uploads";
    public static readonly TimeSpan UploadUrlLifetime = TimeSpan.FromMinutes(10);

    private readonly ITimeLimitedDataProtector _protector =
        dataProtection.CreateProtector("TicketRush.Storage.Upload.v1").ToTimeLimitedDataProtector();

    public string RootPath => Path.GetFullPath(storage.Value.LocalRootPath
        ?? Path.Combine(environment.ContentRootPath, "App_Data", "storage"));

    public UploadTicket CreateUploadUrl(string key, string contentType, long maxBytes)
    {
        var expiresAt = clock.GetUtcNow() + UploadUrlLifetime;
        var grant = string.Join('\n', key, contentType, maxBytes.ToString(CultureInfo.InvariantCulture));
        var token = _protector.Protect(grant, expiresAt);
        var uploadUrl = new Uri($"{frontend.Value.BaseUrl.TrimEnd('/')}{UploadPath}?token={Uri.EscapeDataString(token)}");
        return new UploadTicket(uploadUrl, PublicUrlOf(key), expiresAt);
    }

    public bool IsPublicUrlUnder(string url, string keyPrefix) =>
        url.StartsWith($"{PublicBase}/{keyPrefix}", StringComparison.Ordinal) && !url.Contains("..", StringComparison.Ordinal);

    /// <returns>The grant, or null when the token is forged, tampered with or expired.</returns>
    public UploadGrant? ReadGrant(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        try
        {
            var parts = _protector.Unprotect(token).Split('\n');
            return new UploadGrant(parts[0], parts[1], long.Parse(parts[2], CultureInfo.InvariantCulture));
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    public async Task SaveAsync(string key, Stream content, CancellationToken ct)
    {
        var path = Path.GetFullPath(Path.Combine(RootPath, key));
        if (!path.StartsWith(RootPath + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Storage key escapes the storage root.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await content.CopyToAsync(file, ct);
    }

    private string PublicBase => storage.Value.PublicBaseUrl.TrimEnd('/');

    private Uri PublicUrlOf(string key) => new($"{PublicBase}/{key}");
}
