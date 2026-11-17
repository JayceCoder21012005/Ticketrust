using System.Text.Json;
using System.Text.Json.Serialization;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using TicketRush.Application.Abstractions.Gates;
using TicketRush.Application.Abstractions.Notifications;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Application.Abstractions.Tickets;
using TicketRush.Application.Features.Orders;
using TicketRush.Domain.Gates;
using TicketRush.Domain.Primitives;
using TicketRush.Domain.Tickets;

namespace TicketRush.Application.Features.Gate.Scan;

public sealed record ScanCommand(Guid StaffUserId, Guid EventId, string? Payload);

public sealed class ScanValidator : AbstractValidator<ScanCommand>
{
    public ScanValidator() => RuleFor(c => c.Payload).NotEmpty();
}

/// <summary>What the scan needs of a ticket. Carries the QR secret, so it never leaves this feature.</summary>
internal sealed record ScanTicketRow(Guid Id, Guid EventId, Guid TierId, int? SeatIndex, Guid OwnerUserId, TicketStatus Status, byte[] QrSecret);

/// <summary>
/// <c>POST /gate/events/{eventId}/scan</c> (spec 11). Order of the checks: assignment (404), rate limit and double-read shield,
/// shape of the code (no database), then the ticket: signature, event, freshness, and finally the atomic <c>Valid → CheckedIn</c>
/// UPDATE. Every scan that gets past the guards leaves a <c>check_ins</c> line; a double-read answered from the shield does not,
/// since it is the same scan.
/// </summary>
public sealed class ScanHandler(
    IAppDbContext db,
    IQrCodeService qr,
    IScanGuard guard,
    IUserDirectory users,
    TimeProvider time)
{
    private const int MaxPayloadLength = 512;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<Result<ScanResponse>> HandleAsync(ScanCommand command, CancellationToken ct)
    {
        var gateName = await GateAssignments.FindGateNameAsync(db, command.EventId, command.StaffUserId, ct);
        if (gateName is null)
        {
            return GateErrors.NotAssigned;
        }

        var payload = command.Payload!;
        ScanGate entry;
        try
        {
            entry = await guard.EnterAsync(command.StaffUserId, payload, ct);
        }
        catch (ThrottleUnavailableException)
        {
            return CommonErrors.SystemBusy;
        }

        if (!entry.Allowed)
        {
            return GateErrors.ScanRateLimited(entry.RetryAfter);
        }

        if (entry.Previous is not null && JsonSerializer.Deserialize<ScanResponse>(entry.Previous, Json) is { } previous)
        {
            return previous;
        }

        var response = await DecideAsync(command, gateName, payload, ct);
        await guard.RememberAsync(command.StaffUserId, payload, JsonSerializer.Serialize(response, Json), ct);
        return response;
    }

    private async Task<ScanResponse> DecideAsync(ScanCommand command, string gateName, string payload, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (payload.Length > MaxPayloadLength || !qr.TryParse(payload, out var parsed))
        {
            return await RefuseAsync(command, gateName, null, ScanResult.Invalid, "bad_format", now, ct);
        }

        var row = await db.Tickets.AsNoTracking()
            .Where(t => t.Id == parsed.TicketId)
            .Select(t => new ScanTicketRow(t.Id, t.EventId, t.TierId, t.SeatIndex, t.OwnerUserId, t.Status, t.QrSecret))
            .SingleOrDefaultAsync(ct);
        if (row is null)
        {
            return await RefuseAsync(command, gateName, null, ScanResult.Invalid, "unknown_ticket", now, ct);
        }

        // The signature is checked at the code's own step (so a stale but genuine code is told apart from a forged one),
        // the freshness at the current time.
        if (!qr.Verify(parsed, row.QrSecret, parsed.Step * IQrCodeService.StepSeconds))
        {
            return await RefuseAsync(command, gateName, null, ScanResult.Invalid, "bad_signature", now, ct);
        }

        if (row.EventId != command.EventId)
        {
            return await RefuseAsync(command, gateName, row.Id, ScanResult.WrongEvent, null, now, ct);
        }

        if (!qr.Verify(parsed, row.QrSecret, now.ToUnixTimeSeconds()))
        {
            return await RefuseAsync(command, gateName, row.Id, ScanResult.Expired, null, now, ct);
        }

        return await AdmitAsync(command, gateName, row, now, ct);
    }

    private async Task<ScanResponse> RefuseAsync(
        ScanCommand command,
        string gateName,
        Guid? ticketId,
        ScanResult result,
        string? reason,
        DateTimeOffset now,
        CancellationToken ct)
    {
        db.CheckIns.Add(CheckIn.Record(ticketId, command.EventId, command.StaffUserId, gateName, now, result, reason));
        await db.SaveChangesAsync(ct);
        return new ScanResponse(result, reason, null, null, null);
    }

    private async Task<ScanResponse> AdmitAsync(ScanCommand command, string gateName, ScanTicketRow row, DateTimeOffset now, CancellationToken ct)
    {
        var outcome = await db.InTransactionAsync(token => TryCheckInAsync(command, gateName, row, now, token), ct);
        if (outcome.Result is not (ScanResult.Admitted or ScanResult.AlreadyCheckedIn))
        {
            return outcome;
        }

        return outcome with { Ticket = await LoadInfoAsync(row, ct) };
    }

    /// <summary>The one statement that decides who gets in: a second scan of the same ticket updates no row, whoever is faster.</summary>
    private async Task<ScanResponse> TryCheckInAsync(ScanCommand command, string gateName, ScanTicketRow row, DateTimeOffset now, CancellationToken ct)
    {
        var admitted = await db.Tickets
            .Where(t => t.Id == row.Id && t.EventId == command.EventId && t.Status == TicketStatus.Valid)
            .ExecuteUpdateAsync(
                s => s.SetProperty(t => t.Status, TicketStatus.CheckedIn)
                    .SetProperty(t => t.CheckedInAt, now)
                    .SetProperty(t => t.CheckedInByUserId, command.StaffUserId)
                    .SetProperty(t => t.UpdatedAt, now),
                ct);

        var response = admitted == 1
            ? new ScanResponse(ScanResult.Admitted, null, null, now, null)
            : await ExplainAsync(row.Id, ct);

        db.CheckIns.Add(CheckIn.Record(row.Id, command.EventId, command.StaffUserId, gateName, now, response.Result, response.Reason));
        await db.SaveChangesAsync(ct);
        return response;
    }

    /// <summary>The update touched nothing: say why, from the ticket as it is now (the winner of a race has committed by then).</summary>
    private async Task<ScanResponse> ExplainAsync(Guid ticketId, CancellationToken ct)
    {
        var status = await db.Tickets.AsNoTracking().Where(t => t.Id == ticketId).Select(t => t.Status).SingleAsync(ct);
        if (status != TicketStatus.CheckedIn)
        {
            return new ScanResponse(ScanResult.NotValid, status.ToString().ToLowerInvariant(), null, null, null);
        }

        var first = await db.CheckIns.AsNoTracking()
            .Where(c => c.TicketId == ticketId && c.Result == ScanResult.Admitted)
            .OrderBy(c => c.ScannedAt)
            .Select(c => new { c.ScannedAt, c.GateName })
            .FirstOrDefaultAsync(ct);
        return new ScanResponse(ScanResult.AlreadyCheckedIn, null, null, first?.ScannedAt, first?.GateName);
    }

    private async Task<ScanTicketInfo> LoadInfoAsync(ScanTicketRow row, CancellationToken ct)
    {
        var tier = await db.Events.AsNoTracking()
            .Where(e => e.Id == row.EventId)
            .SelectMany(e => e.Tiers)
            .Where(t => t.Id == row.TierId)
            .Select(t => t.Name)
            .FirstOrDefaultAsync(ct);
        var holder = (await users.GetContactAsync(row.OwnerUserId, ct))?.FullName ?? string.Empty;
        if (row.SeatIndex is not { } index)
        {
            return new ScanTicketInfo(tier ?? string.Empty, null, null, null, holder);
        }

        var labels = await SeatLabels.LoadAsync(db, [row.EventId], [index], ct);
        var label = labels.GetValueOrDefault((row.EventId, index));
        return new ScanTicketInfo(tier ?? string.Empty, label?.Section, label?.Row, label?.Number, holder);
    }
}
