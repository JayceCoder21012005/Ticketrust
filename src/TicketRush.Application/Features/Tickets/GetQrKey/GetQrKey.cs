using Microsoft.EntityFrameworkCore;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Application.Abstractions.Tickets;
using TicketRush.Domain.Primitives;

namespace TicketRush.Application.Features.Tickets.GetQrKey;

/// <summary>
/// <c>GET /tickets/{id}/qr-key</c>: the secret the browser derives the rotating QR from. The throttle comes first and costs a slot
/// even when the answer is 404 or 409, so the endpoint cannot be used to probe ids; when it cannot count (Redis down) the request is
/// refused (503). Only the owner of a <c>Valid</c> ticket of a running, published event gets the secret. It is never logged.
/// </summary>
public sealed class GetQrKeyHandler(IQrKeyThrottle throttle, IAppDbContext db, TimeProvider time)
{
    public async Task<Result<QrKeyResponse>> HandleAsync(Guid userId, Guid ticketId, CancellationToken ct)
    {
        ThrottleResult gate;
        try
        {
            gate = await throttle.TryAcquireAsync(userId, ct);
        }
        catch (ThrottleUnavailableException)
        {
            return CommonErrors.SystemBusy;
        }

        if (!gate.Allowed)
        {
            return TicketErrors.QrKeyRateLimited(gate.RetryAfter);
        }

        var row = await TicketQueries.Owned(db, userId).Where(r => r.Id == ticketId).SingleOrDefaultAsync(ct);
        if (row is null)
        {
            return TicketErrors.NotFound;
        }

        if (!TicketDisplay.CanShowQr(row.Status, row.EventStatus, row.EndsAt, time.GetUtcNow()))
        {
            return TicketErrors.NotDisplayable;
        }

        return await LoadKeyAsync(userId, ticketId, ct);
    }

    private async Task<Result<QrKeyResponse>> LoadKeyAsync(Guid userId, Guid ticketId, CancellationToken ct)
    {
        var secret = await db.Tickets.AsNoTracking()
            .Where(t => t.Id == ticketId && t.OwnerUserId == userId)
            .Select(t => t.QrSecret)
            .SingleOrDefaultAsync(ct);
        return secret is null
            ? TicketErrors.NotFound
            : new QrKeyResponse(ticketId, Convert.ToBase64String(secret), IQrCodeService.StepSeconds, IQrCodeService.Format);
    }
}
