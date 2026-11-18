using FluentValidation;
using Microsoft.EntityFrameworkCore;
using TicketRush.Application.Abstractions.Notifications;
using TicketRush.Application.Abstractions.Persistence;

namespace TicketRush.Application.Features.Tickets.ListMyTickets;

/// <param name="Scope"><c>upcoming</c> (default) or <c>past</c>, case-insensitive.</param>
public sealed record ListMyTicketsQuery(Guid UserId, string? Scope)
{
    public const string Upcoming = "upcoming";
    public const string Past = "past";

    public bool WantsPast => string.Equals(Scope, Past, StringComparison.OrdinalIgnoreCase);

    public static bool IsKnownScope(string? scope) =>
        string.IsNullOrEmpty(scope)
        || string.Equals(scope, Upcoming, StringComparison.OrdinalIgnoreCase)
        || string.Equals(scope, Past, StringComparison.OrdinalIgnoreCase);
}

public sealed class ListMyTicketsValidator : AbstractValidator<ListMyTicketsQuery>
{
    public ListMyTicketsValidator()
    {
        RuleFor(q => q.Scope)
            .Must(ListMyTicketsQuery.IsKnownScope)
            .WithErrorCode("ticket.invalid_scope")
            .WithMessage("Scope must be 'upcoming' or 'past'.");
    }
}

/// <summary>
/// <c>GET /me/tickets</c>: the caller's tickets grouped by event. Upcoming = event not ended and not cancelled and the ticket not
/// voided, sorted by event start ascending; past = everything else, newest event first. At most <see cref="MaxTickets"/> tickets.
/// </summary>
public sealed class ListMyTicketsHandler(IAppDbContext db, IUserDirectory users, TimeProvider time)
{
    public const int MaxTickets = 200;

    public async Task<MyTicketsResponse> HandleAsync(ListMyTicketsQuery query, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var owned = TicketQueries.Owned(db, query.UserId);
        var ordered = query.WantsPast
            ? owned.Where(TicketQueries.IsPast(now)).OrderByDescending(r => r.StartsAt).ThenByDescending(r => r.EventId).ThenBy(r => r.Id)
            : owned.Where(TicketQueries.IsUpcoming(now)).OrderBy(r => r.StartsAt).ThenBy(r => r.EventId).ThenBy(r => r.Id);

        var rows = await ordered.Take(MaxTickets).ToListAsync(ct);
        var items = await new TicketViews(db, users).ToItemsAsync(rows, query.UserId, ct);
        return new MyTicketsResponse(Group(rows, items));
    }

    // The rows come ordered by (start, event, id), so one event's tickets are contiguous and GroupBy keeps that order.
    private static List<TicketEventGroup> Group(List<TicketRow> rows, IReadOnlyList<TicketItem> items) =>
        [.. rows.Zip(items).GroupBy(pair => pair.First.EventId).Select(group =>
        {
            var e = group.First().First.ToSummary();
            return new TicketEventGroup(e.EventId, e.Slug, e.Title, e.CoverImageUrl, e.StartsAt, e.EndsAt, e.VenueName, e.City, e.Status, [.. group.Select(pair => pair.Second)]);
        })];
}
