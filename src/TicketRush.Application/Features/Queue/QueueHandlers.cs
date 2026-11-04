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
            var resolved = await QueueSales.ResolveAsync(sales, command.EventId, now, ct);
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
            var resolved = await QueueSales.ResolveAsync(sales, eventId, now, ct);
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

public sealed class LeaveQueueHandler(ISaleWindowProvider sales, IWaitingRoom room, TimeProvider time)
{
    public async Task<Result> HandleAsync(Guid userId, Guid eventId, CancellationToken ct)
    {
        var resolved = await QueueSales.ResolveAsync(sales, eventId, time.GetUtcNow(), ct);
        if (resolved.IsFailure)
        {
            return resolved.Error;
        }

        try
        {
            await room.LeaveAsync(resolved.Value.Window, userId, ct);
            return Result.Success();
        }
        catch (QueueUnavailableException)
        {
            return CommonErrors.SystemBusy;
        }
    }
}
