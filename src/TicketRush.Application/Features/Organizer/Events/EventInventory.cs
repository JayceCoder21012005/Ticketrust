using System.Text.RegularExpressions;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Application.Abstractions.Security;
using TicketRush.Domain.Events;
using TicketRush.Domain.Events.Layout;
using TicketRush.Domain.Primitives;

namespace TicketRush.Application.Features.Organizer.Events;

// Tiers, layout and phases decide inventory, so all three are draft-only (the aggregate throws event.not_editable).

/// <param name="Price">Whole VND.</param>
public sealed record TierInput(Guid? Id, string Name, long Price, TicketKind Kind, int? Capacity, string Color);

public sealed record ReplaceTiersCommand(Guid OrganizerId, Guid EventId, string Version, IReadOnlyList<TierInput> Tiers);

public sealed class ReplaceTiersValidator : AbstractValidator<ReplaceTiersCommand>
{
    public const int MaxTiers = 20;
    public const long MaxPrice = 100_000_000;

    public ReplaceTiersValidator()
    {
        RuleFor(c => c.Version).Must(OrganizerEvents.IsVersion).WithMessage("Send the version you edited.");
        RuleFor(c => c.Tiers).Must(t => t.Count <= MaxTiers).WithMessage($"At most {MaxTiers} tiers.");
        RuleForEach(c => c.Tiers).ChildRules(tier =>
        {
            tier.RuleFor(t => t.Name).NotEmpty().MaximumLength(100);
            tier.RuleFor(t => t.Price).InclusiveBetween(1, MaxPrice);
            tier.RuleFor(t => t.Kind).IsInEnum();
            tier.RuleFor(t => t.Capacity).NotNull().InclusiveBetween(1, 200_000).When(t => t.Kind == TicketKind.GeneralAdmission);
            tier.RuleFor(t => t.Color).Must(TierColors.IsAllowed).WithMessage("Pick one of the tier colours.");
        });
    }
}

public sealed class ReplaceTiersHandler(IAppDbContext db)
{
    public Task<Result<OrganizerEvent>> HandleAsync(ReplaceTiersCommand command, CancellationToken ct) =>
        OrganizerEvents.ChangeAsync(db, command.EventId, command.OrganizerId, command.Version, evt =>
        {
            evt.ReplaceTiers(command.Tiers
                .Select(t => new TierDraft(t.Id, t.Name, new Money(t.Price), t.Kind, t.Capacity, t.Color.ToLowerInvariant()))
                .ToList());
            return Task.FromResult(Result.Success());
        }, ct);
}

public sealed record ReplaceLayoutCommand(Guid OrganizerId, Guid EventId, string Version, SeatLayoutDefinition Layout);

public sealed class ReplaceLayoutValidator : AbstractValidator<ReplaceLayoutCommand>
{
    public ReplaceLayoutValidator()
    {
        RuleFor(c => c.Version).Must(OrganizerEvents.IsVersion).WithMessage("Send the version you edited.");
        RuleFor(c => c.Layout.Sections).NotNull();
    }
}

public sealed class ReplaceLayoutHandler(IAppDbContext db)
{
    public Task<Result<OrganizerEvent>> HandleAsync(ReplaceLayoutCommand command, CancellationToken ct) =>
        OrganizerEvents.ChangeAsync(db, command.EventId, command.OrganizerId, command.Version, async evt =>
        {
            if (evt.Status != EventStatus.Draft)
            {
                // Frozen inventory beats a validation report about a layout that can't be applied anyway.
                throw new DomainException("event.not_editable", $"Operation is not allowed while the event is {evt.Status}.");
            }

            var errors = SeatLayoutGenerator.Validate(command.Layout, evt.Tiers.ToDictionary(t => t.Id, t => t.Kind));
            if (errors.Count > 0)
            {
                return Error.Validation("layout.invalid", "The layout has errors.", errors
                    .GroupBy(e => e.Section.Length == 0 ? e.Field : $"{e.Section}.{e.Field}")
                    .ToDictionary(g => g.Key, g => g.Select(e => e.Message).ToArray()));
            }

            var seats = evt.ReplaceLayout(command.Layout);

            // Old and new seats change in the one SaveChanges transaction, so readers never see an empty map.
            var previous = await db.EventSeats.Where(s => s.EventId == evt.Id).ToListAsync(ct);
            db.EventSeats.RemoveRange(previous);
            db.EventSeats.AddRange(seats.Select(s =>
                EventSeat.Create(evt.Id, s.Index, s.Section, s.Row, s.Number, s.X, s.Y, s.TierId)));
            return Result.Success();
        }, ct);
}

/// <param name="AccessCode">null keeps the phase's current code (matched by <see cref="Id"/>), "" removes it, a value sets it.</param>
public sealed record PhaseInput(
    Guid? Id,
    string Name,
    DateTimeOffset OpensAt,
    DateTimeOffset ClosesAt,
    bool QueueEnabled,
    int MaxTicketsPerUser,
    string? AccessCode);

public sealed record ReplacePhasesCommand(Guid OrganizerId, Guid EventId, string Version, IReadOnlyList<PhaseInput> Phases);

public sealed partial class ReplacePhasesValidator : AbstractValidator<ReplacePhasesCommand>
{
    public const int MaxPhases = 10;

    public ReplacePhasesValidator()
    {
        RuleFor(c => c.Version).Must(OrganizerEvents.IsVersion).WithMessage("Send the version you edited.");
        RuleFor(c => c.Phases).Must(p => p.Count <= MaxPhases).WithMessage($"At most {MaxPhases} sale phases.");
        RuleForEach(c => c.Phases).ChildRules(phase =>
        {
            phase.RuleFor(p => p.Name).NotEmpty().MaximumLength(100);
            phase.RuleFor(p => p.ClosesAt).GreaterThan(p => p.OpensAt).WithMessage("A phase must close after it opens.");
            phase.RuleFor(p => p.MaxTicketsPerUser).InclusiveBetween(SalePhase.MinTicketsPerUser, SalePhase.MaxTicketsPerUserLimit);
            phase.RuleFor(p => p.AccessCode).Must(code => AccessCode().IsMatch(code!))
                .When(p => !string.IsNullOrEmpty(p.AccessCode))
                .WithMessage("Use 4–32 letters, digits or dashes.");
        });
    }

    [GeneratedRegex("^[A-Za-z0-9-]{4,32}$")]
    private static partial Regex AccessCode();
}

public sealed class ReplacePhasesHandler(IAppDbContext db, IAccessCodeHasher hasher)
{
    public Task<Result<OrganizerEvent>> HandleAsync(ReplacePhasesCommand command, CancellationToken ct) =>
        OrganizerEvents.ChangeAsync(db, command.EventId, command.OrganizerId, command.Version, evt =>
        {
            evt.ReplacePhases(command.Phases.Select(p => new PhaseDraft(
                    p.Id,
                    p.Name,
                    p.OpensAt,
                    p.ClosesAt,
                    p.QueueEnabled,
                    p.MaxTicketsPerUser,
                    string.IsNullOrEmpty(p.AccessCode) ? null : hasher.Hash(p.AccessCode),
                    KeepExistingCode: p.AccessCode is null))
                .ToList());
            return Task.FromResult(Result.Success());
        }, ct);
}
