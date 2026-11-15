using TicketRush.Application.Abstractions.Queue;

namespace TicketRush.Application.Features.Queue;

/// <summary>Chooses which phase of an event the waiting room is about right now.</summary>
public static class PhaseSelector
{
    /// <returns>The open phase, else the next one to open, else the last one that closed; null without phases.</returns>
    public static SaleWindow? Select(IEnumerable<SaleWindow> phases, DateTimeOffset now)
    {
        var all = phases.ToList();
        return all.Where(p => p.OpensAt <= now && now < p.ClosesAt).MinBy(p => p.OpensAt)
            ?? all.Where(p => p.OpensAt > now).MinBy(p => p.OpensAt)
            ?? all.MaxBy(p => p.ClosesAt);
    }

    /// <summary>
    /// The phase a user who is in no room of the event yet is queued for. While a phase is open and the next queued
    /// phase is inside its pre-queue window, that next phase wins (its random pre-queue is running); otherwise this is
    /// <see cref="Select"/>.
    /// </summary>
    public static SaleWindow? SelectJoinTarget(IEnumerable<SaleWindow> phases, DateTimeOffset now, TimeSpan preQueue)
    {
        var all = phases.ToList();
        return Ambiguous(all, now, preQueue)?.Next ?? Select(all, now);
    }

    /// <summary>
    /// The rooms a user could already be in: the open phase and the next one when both exist and the next one's
    /// pre-queue is running; otherwise the single phase of the moment (empty without phases).
    /// </summary>
    public static IReadOnlyList<SaleWindow> RoomCandidates(IEnumerable<SaleWindow> phases, DateTimeOffset now, TimeSpan preQueue)
    {
        var all = phases.ToList();
        if (Ambiguous(all, now, preQueue) is var (open, next))
        {
            return [open, next];
        }

        return Select(all, now) is { } only ? [only] : [];
    }

    private static (SaleWindow Open, SaleWindow Next)? Ambiguous(List<SaleWindow> all, DateTimeOffset now, TimeSpan preQueue)
    {
        var open = all.Where(p => p.OpensAt <= now && now < p.ClosesAt).MinBy(p => p.OpensAt);
        var next = all.Where(p => p.QueueEnabled && p.OpensAt > now).MinBy(p => p.OpensAt);
        return open is not null && next is not null && next.OpensAt - now <= preQueue ? (open, next) : null;
    }
}

/// <summary>The server decides how often each client polls (spec 06): far back in line means rarely.</summary>
public static class PollSchedule
{
    private const int MaxPreSleepMs = 120_000;
    private const int MinPreSleepMs = 1_000;
    private const int PreSpreadMs = 3_000;
    private const int StillShufflingMinMs = 2_000;

    public static int NextPollMs(QueueClientStatus status, long position, DateTimeOffset now, DateTimeOffset opensAt, Func<double> random)
    {
        if (status == QueueClientStatus.Pre)
        {
            // Wake at opensAt + random(0-3 s) so the whole pre-queue does not hit the server in the same instant.
            var untilWake = (opensAt - now).TotalMilliseconds + (random() * PreSpreadMs);
            if (untilWake <= 0)
            {
                // Opening time has passed but the room still says Pre (shuffle running, or a failover): never answer
                // with a constant, or the whole crowd comes back in lockstep every second.
                return StillShufflingMinMs + (int)(random() * PreSpreadMs);
            }

            return (int)Math.Clamp(untilWake, MinPreSleepMs, MaxPreSleepMs);
        }

        var baseMs = status switch
        {
            QueueClientStatus.Waiting => position switch
            {
                > 10_000 => 15_000,
                > 1_000 => 8_000,
                > 100 => 4_000,
                _ => 2_000,
            },
            QueueClientStatus.Admitted => 30_000,
            QueueClientStatus.AllHeld => 5_000,
            QueueClientStatus.SoldOut => 15_000,
            _ => 60_000,
        };

        return (int)(baseMs * (0.8 + (0.4 * random())));
    }
}

/// <summary>Turns the raw Redis state into what a buyer's client needs. Pure, so every rule is unit-tested.</summary>
public static class QueueStatusMapper
{
    /// <returns>Null when the user never joined (and the sale is not over).</returns>
    public static QueueStatusResponse? Map(
        SaleWindow sale,
        ClaimResult claim,
        DateTimeOffset now,
        double rateEma,
        int fallbackRatePerSecond,
        Func<double> random)
    {
        QueueStatusResponse Build(QueueClientStatus status, long? position = null, int? eta = null) => new(
            status,
            position,
            position is null ? null : Math.Max(0, position.Value - 1),
            eta,
            sale.OpensAt,
            now,
            PollSchedule.NextPollMs(status, position ?? 0, now, sale.OpensAt, random),
            status == QueueClientStatus.Admitted ? claim.Token : null,
            status == QueueClientStatus.Admitted ? claim.ActiveUntil : null);

        if (claim.Active)
        {
            return Build(QueueClientStatus.Admitted);
        }

        if (now >= sale.ClosesAt || claim.Status == RoomStatus.Closed)
        {
            return Build(QueueClientStatus.Closed);
        }

        if (claim.Done)
        {
            return Build(QueueClientStatus.Expired);
        }

        if (claim.InPre && claim.Number is null)
        {
            return Build(QueueClientStatus.Pre);
        }

        if (claim.Number is not { } number)
        {
            return null;
        }

        return claim.Status switch
        {
            RoomStatus.AllHeld => Build(QueueClientStatus.AllHeld),
            RoomStatus.SoldOut => Build(QueueClientStatus.SoldOut),
            _ => BuildWaiting(number),
        };

        QueueStatusResponse BuildWaiting(long ticket)
        {
            // Someone who is due but waits for a free slot still shows as "next".
            var position = Math.Max(1, ticket - claim.Admitted);
            var rate = rateEma > 0 ? rateEma : fallbackRatePerSecond;
            return Build(QueueClientStatus.Waiting, position, (int)Math.Ceiling(position / rate));
        }
    }
}
