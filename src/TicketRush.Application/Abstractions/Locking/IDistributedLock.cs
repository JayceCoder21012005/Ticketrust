namespace TicketRush.Application.Abstractions.Locking;

/// <summary>
/// Cluster-wide mutual exclusion with a TTL, used for leader election of singleton jobs (C12).
/// A crashed holder loses the lock when its TTL expires; a live holder renews it in the background.
/// </summary>
public interface IDistributedLock
{
    /// <returns>A handle while the lock is held, or <c>null</c> when someone else holds it.</returns>
    Task<IDistributedLockHandle?> TryAcquireAsync(string name, TimeSpan ttl, CancellationToken ct);
}

public interface IDistributedLockHandle : IAsyncDisposable
{
    string Name { get; }

    /// <summary>Cancelled when renewal fails and the lock may now belong to someone else. Stop work immediately.</summary>
    CancellationToken LockLost { get; }
}
