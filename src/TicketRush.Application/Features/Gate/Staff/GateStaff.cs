using FluentValidation;
using Microsoft.EntityFrameworkCore;
using TicketRush.Application.Abstractions.Notifications;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Domain.Events;
using TicketRush.Domain.Gates;
using TicketRush.Domain.Primitives;

namespace TicketRush.Application.Features.Gate.Staff;

public sealed record AssignGateStaffCommand(Guid OrganizerId, Guid EventId, string Email, string GateName);

public sealed class AssignGateStaffValidator : AbstractValidator<AssignGateStaffCommand>
{
    public AssignGateStaffValidator()
    {
        RuleFor(c => c.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(c => c.GateName).NotEmpty().MaximumLength(GateAssignment.GateNameMaxLength).Must(n => !string.IsNullOrWhiteSpace(n));
    }
}

public sealed record RemoveGateStaffCommand(Guid OrganizerId, Guid EventId, string Email);

public sealed class RemoveGateStaffValidator : AbstractValidator<RemoveGateStaffCommand>
{
    public RemoveGateStaffValidator() => RuleFor(c => c.Email).NotEmpty().EmailAddress().MaximumLength(256);
}

internal static class OwnedEvents
{
    /// <summary>Another organizer's event is indistinguishable from a missing one (404).</summary>
    public static Task<bool> ExistsAsync(IAppDbContext db, Guid eventId, Guid organizerId, CancellationToken ct) =>
        db.Events.AsNoTracking().AnyAsync(e => e.Id == eventId && e.OrganizerId == organizerId, ct);
}

/// <summary><c>POST /organizer/events/{id}/gate-staff</c>: idempotent. Assigning an assigned user only renames their gate.</summary>
public sealed class AssignGateStaffHandler(IAppDbContext db, IUserDirectory users, TimeProvider time)
{
    public async Task<Result> HandleAsync(AssignGateStaffCommand command, CancellationToken ct)
    {
        if (!await OwnedEvents.ExistsAsync(db, command.EventId, command.OrganizerId, ct))
        {
            return EventErrors.NotFound;
        }

        if (await users.FindGateStaffIdAsync(command.Email.Trim(), ct) is not { } staffId)
        {
            return GateErrors.StaffNotFound;
        }

        var existing = await db.GateAssignments.SingleOrDefaultAsync(a => a.EventId == command.EventId && a.UserId == staffId, ct);
        if (existing is null)
        {
            db.GateAssignments.Add(GateAssignment.Create(command.EventId, staffId, command.GateName, time.GetUtcNow()));
        }
        else
        {
            existing.Rename(command.GateName);
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (existing is null)
        {
            // Two requests assigned the same person at once: the key stopped the second one, and the first one's row is there.
            db.ChangeTracker.Clear();
        }

        return Result.Success();
    }
}

/// <summary><c>DELETE /organizer/events/{id}/gate-staff</c>: idempotent. The person's past scans stay in the log.</summary>
public sealed class RemoveGateStaffHandler(IAppDbContext db, IUserDirectory users)
{
    public async Task<Result> HandleAsync(RemoveGateStaffCommand command, CancellationToken ct)
    {
        if (!await OwnedEvents.ExistsAsync(db, command.EventId, command.OrganizerId, ct))
        {
            return EventErrors.NotFound;
        }

        if (await users.FindGateStaffIdAsync(command.Email.Trim(), ct) is not { } staffId)
        {
            return Result.Success();
        }

        await db.GateAssignments.Where(a => a.EventId == command.EventId && a.UserId == staffId).ExecuteDeleteAsync(ct);
        return Result.Success();
    }
}
