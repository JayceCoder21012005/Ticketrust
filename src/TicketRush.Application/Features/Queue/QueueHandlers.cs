using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using TicketRush.Application.Abstractions.Queue;
using TicketRush.Application.Abstractions.Security;
using TicketRush.Application.Options;
using TicketRush.Domain.Events;
using TicketRush.Domain.Primitives;

namespace TicketRush.Application.Features.Queue;

internal static class QueueSales
{
    /// <summary>The phase the waiting room is about, with its access-code hash; fails with event.not_found for hidden events.</summary>
    public static async Task<Result<SalePhaseInfo>> ResolveAsync(ISaleWindowProvider provider, Guid eventId, DateTimeOffset now, CancellationToken ct)
    {
        if (await provider.GetAsync(eventId, ct) is not { } sales)
        {
            return EventErrors.NotFound;
        }

        var selected = PhaseSelector.Select(sales.Phases.Select(p => p.Window), now);
        return selected is null
            ? QueueErrors.NotOpen
            : sales.Phases.First(p => p.Window.PhaseId == selected.PhaseId);
    }

    /// <summary>
    /// The phase of the event this user's room is about (design rule 2). A user who is already in a room keeps it;
    /// only someone in none gets the join target. Without competing phases there is one candidate and no Redis call.
    /// Two passes over the candidates: active membership first, then (<paramref name="includeDone"/>, for status and
    /// leave) the room the user left or expired from, so they still see Expired there. A join is never "done"-aware:
    /// someone who finished the open phase is a new joiner for the next one.
    /// </summary>
    public static async Task<Result<SalePhaseInfo>> ResolveForUserAsync(
        ISaleWindowProvider provider,
        IWaitingRoom room,
        QueueOptions options,
        Guid eventId,
        Guid userId,
        DateTimeOffset now,
        bool includeDone,
        CancellationToken ct)
    {
        if (await provider.GetAsync(eventId, ct) is not { } sales)
        {
            return EventErrors.NotFound;
        }

        var windows = sales.Phases.Select(p => p.Window).ToList();
        var preQueue = TimeSpan.FromMinutes(options.PreQueueMinutes);
        var candidates = PhaseSelector.RoomCandidates(windows, now, preQueue);
        if (candidates.Count == 0)
        {
            return QueueErrors.NotOpen;
        }

        var chosen = candidates.Count == 1 ? candidates[0] : await FirstRoomOfAsync(candidates, w => room.IsMemberAsync(w, userId, ct));
        if (chosen is null && includeDone)
        {
            chosen = await FirstRoomOfAsync(candidates, w => room.IsDoneAsync(w, userId, ct));
        }

        chosen ??= PhaseSelector.SelectJoinTarget(windows, now, preQueue)!;
        return sales.Phases.First(p => p.Window.PhaseId == chosen.PhaseId);
    }

    private static async Task<SaleWindow?> FirstRoomOfAsync(IReadOnlyList<SaleWindow> candidates, Func<SaleWindow, Task<bool>> isIn)
    {
        foreach (var candidate in candidates)
        {
            if (await isIn(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public static async Task<Result<T>> GuardAsync<T>(Func<Task<Result<T>>> action)
    {
        try
        {
            return await action();
        }
        catch (QueueUnavailableException)
        {
            // Fail closed: when Redis is unreachable nobody is admitted and nothing falls back to Postgres.
            return CommonErrors.SystemBusy;
        }
    }
}

public sealed class JoinQueueHandler(
    ISaleWindowProvider sales,
    IWaitingRoom room,
    IAccessCodeHasher hasher,
    IOptions<QueueOptions> options,
    TimeProvider time)
{
    public Task<Result<QueueStatusResponse>> HandleAsync(JoinQueueCommand command, CancellationToken ct) =>
        QueueSales.GuardAsync(async () =>
        {
            if (!command.EmailVerified)
            {
                return QueueErrors.EmailNotConfirmed;
            }

            var now = time.GetUtcNow();
            var resolved = await QueueSales.ResolveForUserAsync(sales, room, options.Value, command.EventId, command.UserId, now, includeDone: false, ct);
            if (resolved.IsFailure)
            {
                return resolved.Error;
            }

            var (sale, accessCodeHash) = (resolved.Value.Window, resolved.Value.AccessCodeHash);
            if (now >= sale.ClosesAt)
            {
                return QueueErrors.Closed;
            }

            // Queued sales open their room early so the pre-queue can form; others open exactly at opensAt.
            var joinOpensAt = sale.QueueEnabled ? sale.OpensAt - TimeSpan.FromMinutes(options.Value.PreQueueMinutes) : sale.OpensAt;
            if (now < joinOpensAt)
            {
                return QueueErrors.NotOpen;
            }

            // A member reloading the page or opening a second tab proved the code when they joined.
            if (accessCodeHash is not null && !CodeMatches(command.AccessCode, accessCodeHash) && !await room.IsMemberAsync(sale, command.UserId, ct))
            {
                return QueueErrors.InvalidAccessCode;
            }

            await room.JoinAsync(sale, command.UserId, ct);
            var claim = await room.StatusAsync(sale, command.UserId, ct);
            var rate = await room.GetRateEmaAsync(sale.PhaseId, ct);
            return QueueStatusMapper.Map(sale, claim, now, rate, options.Value.AdmissionRatePerSecond, Random.Shared.NextDouble)
                ?? Result.Failure<QueueStatusResponse>(QueueErrors.NotJoined);
        });

    private bool CodeMatches(string? code, string expectedHash)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(hasher.Hash(code)), Encoding.UTF8.GetBytes(expectedHash));
    }
}

public sealed class GetQueueStatusHandler(
    ISaleWindowProvider sales,
    IWaitingRoom room,
    IOptions<QueueOptions> options,
    TimeProvider time)
{
    public Task<Result<QueueStatusResponse>> HandleAsync(Guid userId, Guid eventId, CancellationToken ct) =>
        QueueSales.GuardAsync(async () =>
        {
            var now = time.GetUtcNow();
            var resolved = await QueueSales.ResolveForUserAsync(sales, room, options.Value, eventId, userId, now, includeDone: true, ct);
            if (resolved.IsFailure)
            {
                return resolved.Error;
            }

            var sale = resolved.Value.Window;
            var claim = await room.StatusAsync(sale, userId, ct);
            var rate = claim.Number is null ? 0 : await room.GetRateEmaAsync(sale.PhaseId, ct);
            return QueueStatusMapper.Map(sale, claim, now, rate, options.Value.AdmissionRatePerSecond, Random.Shared.NextDouble)
                ?? Result.Failure<QueueStatusResponse>(QueueErrors.NotJoined);
        });
}

public sealed class LeaveQueueHandler(ISaleWindowProvider sales, IWaitingRoom room, IOptions<QueueOptions> options, TimeProvider time)
{
    public async Task<Result> HandleAsync(Guid userId, Guid eventId, CancellationToken ct)
    {
        try
        {
            // The membership check can reach Redis, so resolving happens inside the guard.
            var resolved = await QueueSales.ResolveForUserAsync(sales, room, options.Value, eventId, userId, time.GetUtcNow(), includeDone: true, ct);
            if (resolved.IsFailure)
            {
                return resolved.Error;
            }

            await room.LeaveAsync(resolved.Value.Window, userId, ct);
            return Result.Success();
        }
        catch (QueueUnavailableException)
        {
            return CommonErrors.SystemBusy;
        }
    }
}
