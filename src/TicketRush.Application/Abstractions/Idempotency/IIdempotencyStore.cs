namespace TicketRush.Application.Abstractions.Idempotency;

/// <summary>Outcome of claiming an Idempotency-Key for one request.</summary>
public abstract record IdempotencyBegin
{
    /// <summary>First use of the key: run the request, then complete or abandon.</summary>
    public sealed record Started : IdempotencyBegin;

    /// <summary>The first request with this key is still running.</summary>
    public sealed record InProgress : IdempotencyBegin;

    /// <summary>The key was used with a different request.</summary>
    public sealed record Mismatch : IdempotencyBegin;

    /// <summary>The key already answered this request: send the same response again.</summary>
    public sealed record Replay(int Status, string ContentType, byte[] Body) : IdempotencyBegin;
}

/// <summary>Remembers the response to each (user, Idempotency-Key) for a while (spec 00, C2).</summary>
public interface IIdempotencyStore
{
    Task<IdempotencyBegin> BeginAsync(Guid userId, Guid key, string fingerprint, TimeSpan ttl, CancellationToken ct);

    Task CompleteAsync(Guid userId, Guid key, string fingerprint, int status, string contentType, byte[] body, TimeSpan ttl, CancellationToken ct);

    /// <summary>Forgets a key whose request failed on the server, so a retry runs again.</summary>
    Task AbandonAsync(Guid userId, Guid key, CancellationToken ct);
}

public sealed class IdempotencyUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
