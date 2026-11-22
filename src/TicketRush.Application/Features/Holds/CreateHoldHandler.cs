using Microsoft.Extensions.Options;
using TicketRush.Application.Abstractions.Inventory;
using TicketRush.Application.Features.Queue;
using TicketRush.Application.Options;
using TicketRush.Domain.Events;
using TicketRush.Domain.Primitives;

namespace TicketRush.Application.Features.Holds;

/// <summary>
/// <c>POST /events/{id}/holds</c> (spec 07): checks the selection against the published layout, then claims it in Redis in
/// one atomic step. Best available tries up to three candidate runs. Redis down → <c>system.busy</c> (fail closed).
/// </summary>
public sealed class CreateHoldHandler(
    IHoldCatalog catalog,
    IInventoryStore store,
    IInventorySeeder seeder,
    IBestAvailablePicker picker,
    Abstractions.Dashboard.IDashboardStore dashboard,
    Abstractions.Observability.ISalesMetrics metrics,
    IOptions<HoldsOptions> options,
    TimeProvider time)
{
    private const int BestAvailableAttempts = 3;

    public async Task<Result<HoldResponse>> HandleAsync(CreateHoldCommand command, CancellationToken ct)
    {
        if (await catalog.GetAsync(command.EventId, ct) is not { } entry)
        {
            return EventErrors.NotFound;
        }

        var now = time.GetUtcNow();
        if (CheckPhase(entry, command.PhaseId, now) is { } phaseError)
        {
            return phaseError;
        }

        var selection = HoldSelection.Validate(entry, command, options.Value.MaxSeatsPerHold);
        if (selection.IsFailure)
        {
            return selection.Error;
        }

        try
        {
            if (!await store.IsWarmAsync(entry.EventId, ct))
            {
                await seeder.SeedAsync(entry.EventId, ct);
            }

            var maxTickets = entry.Phases.First(p => p.Id == command.PhaseId).MaxTicketsPerUser;
            var held = command.BestAvailable is { } best
                ? await HoldBestAsync(entry, command, selection.Value, best, maxTickets, now, ct)
                : await HoldAsync(entry, command, selection.Value, maxTickets, now, ct);
            if (held.IsSuccess)
            {
                // Spec 15's funnel step "held" (best effort: it never fails the hold).
                await dashboard.RecordHeldAsync(entry.EventId, ct);
            }

            metrics.HoldCreated(held.IsSuccess ? "ok" : held.Error.Code);
            return held;
        }
        catch (InventoryUnavailableException)
        {
            metrics.HoldCreated(CommonErrors.SystemBusy.Code);
            return CommonErrors.SystemBusy;
        }
    }

    private static Error? CheckPhase(HoldCatalogEntry entry, Guid phaseId, DateTimeOffset now)
    {
        var phase = entry.Phases.FirstOrDefault(p => p.Id == phaseId);
        if (phase is null)
        {
            return QueueErrors.AdmissionInvalid;
        }

        if (now >= phase.ClosesAt)
        {
            return QueueErrors.Closed;
        }

        return now < phase.OpensAt ? QueueErrors.NotOpen : null;
    }

    private async Task<Result<HoldResponse>> HoldAsync(
        HoldCatalogEntry entry, CreateHoldCommand command, HoldSelection selection, int maxTickets, DateTimeOffset now, CancellationToken ct)
    {
        var outcome = await store.CreateHoldAsync(Request(entry, command, selection.Seats, selection.Ga, maxTickets, now), ct);
        return ToResult(entry, outcome, selection.Seats, selection.Ga, now);
    }

    private async Task<Result<HoldResponse>> HoldBestAsync(
        HoldCatalogEntry entry, CreateHoldCommand command, HoldSelection selection, BestAvailableRequest best, int maxTickets, DateTimeOffset now, CancellationToken ct)
    {
        var snapshot = await store.GetSnapshotAsync(entry.EventId, entry.GaTierIds, ct);
        var taken = snapshot.Held.Concat(snapshot.Sold).ToHashSet();
        foreach (var candidate in picker.Candidates(entry, taken, best.TierId, best.Quantity, BestAvailableAttempts))
        {
            var outcome = await store.CreateHoldAsync(Request(entry, command, candidate, selection.Ga, maxTickets, now), ct);
            if (outcome.Status != HoldStatus.SeatUnavailable)
            {
                return ToResult(entry, outcome, candidate, selection.Ga, now);
            }
        }

        return HoldErrors.InsufficientQuantity(best.TierId);
    }

    private HoldRequest Request(HoldCatalogEntry entry, CreateHoldCommand command, IReadOnlyList<int> seats, IReadOnlyList<GaLine> ga, int maxTickets, DateTimeOffset now) =>
        new(entry.EventId, command.UserId, seats, ga, entry.GaTierIds, maxTickets, command.ReplaceExisting, now, options.Value.Ttl);

    private static Result<HoldResponse> ToResult(HoldCatalogEntry entry, HoldOutcome outcome, IReadOnlyList<int> seats, IReadOnlyList<GaLine> ga, DateTimeOffset now) =>
        outcome.Status switch
        {
            HoldStatus.Ok => HoldPricing.ToResponse(entry, outcome.HoldId!.Value, seats, ga, outcome.ExpiresAt!.Value, now),
            HoldStatus.SeatUnavailable => HoldErrors.SeatsUnavailable(outcome.Unavailable),
            HoldStatus.GaInsufficient => outcome.Tier is { } tier ? HoldErrors.InsufficientQuantity(tier) : HoldErrors.InsufficientQuantityAny,
            HoldStatus.LimitExceeded => HoldErrors.LimitExceeded,
            _ => HoldErrors.AlreadyExists,
        };
}

/// <summary>A validated selection: distinct known seats and positive GA lines of GA tiers.</summary>
internal sealed record HoldSelection(IReadOnlyList<int> Seats, IReadOnlyList<GaLine> Ga)
{
    public static Result<HoldSelection> Validate(HoldCatalogEntry entry, CreateHoldCommand command, int maxSeatsPerHold)
    {
        var seats = (command.SeatIndexes ?? []).Distinct().Order().ToList();
        var ga = command.Ga ?? [];
        var best = command.BestAvailable;
        var tiers = entry.Tiers.ToDictionary(t => t.Id);
        var knownSeats = entry.Seats.Select(s => s.Index).ToHashSet();

        if (seats.Any(i => !knownSeats.Contains(i))
            || ga.Any(l => l.Quantity < 0 || !tiers.TryGetValue(l.TierId, out var tier) || tier.Kind != TicketKind.GeneralAdmission)
            || (best is not null && (best.Quantity < 0 || !tiers.TryGetValue(best.TierId, out var bestTier) || bestTier.Kind != TicketKind.Seated || seats.Count > 0)))
        {
            return HoldErrors.InvalidSeat;
        }

        var gaLines = ga.Where(l => l.Quantity > 0).GroupBy(l => l.TierId).Select(g => new GaLine(g.Key, g.Sum(l => l.Quantity))).ToList();
        var count = seats.Count + gaLines.Sum(l => l.Quantity) + (best?.Quantity ?? 0);
        if (count == 0)
        {
            return HoldErrors.EmptySelection;
        }

        return count > maxSeatsPerHold ? HoldErrors.LimitExceeded : new HoldSelection(seats, gaLines);
    }
}
