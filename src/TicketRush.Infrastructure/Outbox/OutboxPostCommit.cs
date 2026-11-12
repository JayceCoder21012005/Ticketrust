namespace TicketRush.Infrastructure.Outbox;

/// <summary>
/// Side effects that must not happen before the outbox batch commits: the live pushes of the notification handlers. A push sent
/// inside the batch transaction can reach a tab seconds before its row exists (mark-read answers 404, a refetch drops the "ghost"
/// row), and pushes again with a new id if the batch rolls back; and a slow backplane would keep the batch transaction open.
/// <para>
/// <see cref="OutboxProcessor"/> opens one collector per batch, in an <see cref="AsyncLocal{T}"/>, so it is visible exactly to the
/// handlers of that batch (they run in its async flow) and to nothing else. <c>UserNotifier</c> defers its sends to it while one is
/// open and sends at once otherwise (the Api's own code paths). The processor drops what a handler deferred when that handler's
/// savepoint is rolled back, and runs the rest after the commit; if the batch fails, nothing runs.
/// </para>
/// </summary>
internal sealed class OutboxPostCommit
{
    private static readonly AsyncLocal<OutboxPostCommit?> Active = new();

    private readonly List<Func<CancellationToken, Task>> _actions = [];
    private readonly Lock _gate = new();

    /// <summary>The collector of the batch running in this async flow, or <c>null</c> outside a batch.</summary>
    public static OutboxPostCommit? Current => Active.Value;

    /// <summary>Opens a collector for the calling async flow (and the flows it starts) until <see cref="Close"/>.</summary>
    public static OutboxPostCommit Open() => Active.Value = new OutboxPostCommit();

    public static void Close() => Active.Value = null;

    /// <summary>Queues <paramref name="action"/> for after the batch commits.</summary>
    /// <returns><c>false</c> when no batch is open here: the caller must run the action itself, now.</returns>
    public static bool TryDefer(Func<CancellationToken, Task> action)
    {
        if (Active.Value is not { } batch)
        {
            return false;
        }

        lock (batch._gate)
        {
            batch._actions.Add(action);
        }

        return true;
    }

    /// <summary>A position to roll back to with <see cref="DropFrom"/> (taken before a handler runs).</summary>
    public int Mark()
    {
        lock (_gate)
        {
            return _actions.Count;
        }
    }

    /// <summary>Forgets everything queued since <paramref name="mark"/>: the handler that queued it was rolled back.</summary>
    public void DropFrom(int mark)
    {
        lock (_gate)
        {
            _actions.RemoveRange(mark, _actions.Count - mark);
        }
    }

    /// <summary>The surviving actions, in the order they were queued.</summary>
    public IReadOnlyList<Func<CancellationToken, Task>> Actions()
    {
        lock (_gate)
        {
            return [.. _actions];
        }
    }
}
