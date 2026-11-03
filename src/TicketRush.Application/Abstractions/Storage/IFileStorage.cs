namespace TicketRush.Application.Abstractions.Storage;

/// <param name="UploadUrl">Where the browser PUTs the file (presigned, short-lived).</param>
/// <param name="PublicUrl">Where the file can be read once uploaded.</param>
public sealed record UploadTicket(Uri UploadUrl, Uri PublicUrl, DateTimeOffset ExpiresAt);

public interface IFileStorage
{
    /// <summary>Presigns a PUT for <paramref name="key"/>; the upload must match the content type and size.</summary>
    UploadTicket CreateUploadUrl(string key, string contentType, long maxBytes);

    /// <summary>True when <paramref name="url"/> points at a file under <paramref name="keyPrefix"/> in this storage.</summary>
    bool IsPublicUrlUnder(string url, string keyPrefix);
}
