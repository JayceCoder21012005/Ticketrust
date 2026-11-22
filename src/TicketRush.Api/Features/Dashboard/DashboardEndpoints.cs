using System.Globalization;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using TicketRush.Api.Common.Auth;
using TicketRush.Api.Common.Http;
using TicketRush.Application.Features.Dashboard;
using TicketRush.Domain.Orders;
using TicketRush.Infrastructure.Identity;

namespace TicketRush.Api.Features.Dashboard;

public sealed record SalesSeriesRequest(
    [property: FromQuery(Name = "from")] DateTimeOffset? From,
    [property: FromQuery(Name = "to")] DateTimeOffset? To,
    [property: FromQuery(Name = "bucket")] string? Bucket);

public sealed record EventOrdersRequest(
    [property: FromQuery(Name = "cursor")] string? Cursor,
    [property: FromQuery(Name = "status")] OrderStatus? Status,
    [property: FromQuery(Name = "limit")] int? Limit);

/// <summary>The organizer's realtime dashboard (spec 15). Every route answers 404 for an event the caller does not own.</summary>
public static class DashboardEndpoints
{
    private static readonly string[] Header =
        ["Mã đơn", "Người mua", "Email người mua", "Người giữ vé", "Hạng vé", "Khu", "Hàng", "Ghế", "Thanh toán lúc", "Check-in", "Check-in lúc"];

    public static RouteGroupBuilder MapDashboardEndpoints(this RouteGroupBuilder v1)
    {
        var events = v1.MapGroup("/organizer/events/{id:guid}").WithTags("Dashboard")
            .RequireAuthorization(AuthPolicies.RequireOrganizer).AddEndpointFilter<NoStoreFilter>();
        events.MapGet("/dashboard", GetAsync).WithName("GetDashboard").Produces<DashboardSnapshot>().ProducesProblem(StatusCodes.Status404NotFound);
        events.MapGet("/sales-series", SeriesAsync).WithName("GetSalesSeries").Produces<SalesSeries>().ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);
        events.MapGet("/orders", OrdersAsync).WithName("ListEventOrders").Produces<EventOrderPage>().ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);
        events.MapGet("/export.csv", ExportAsync).WithName("ExportAttendees").Produces(StatusCodes.Status200OK, contentType: "text/csv")
            .ProducesProblem(StatusCodes.Status404NotFound);
        return v1;
    }

    private static async Task<IResult> GetAsync(Guid id, ClaimsPrincipal user, GetDashboardHandler handler, CancellationToken ct) =>
        (await handler.HandleAsync(user.UserId(), id, ct)).ToHttpResult();

    private static async Task<IResult> SeriesAsync(Guid id, [AsParameters] SalesSeriesRequest request, ClaimsPrincipal user, GetSalesSeriesHandler handler, CancellationToken ct) =>
        (await handler.HandleAsync(new SalesSeriesQuery(user.UserId(), id, request.From, request.To, request.Bucket), ct)).ToHttpResult();

    private static async Task<IResult> OrdersAsync(Guid id, [AsParameters] EventOrdersRequest request, ClaimsPrincipal user, ListEventOrdersHandler handler, CancellationToken ct) =>
        (await handler.HandleAsync(new ListEventOrdersQuery(user.UserId(), id, request.Cursor, request.Status, request.Limit ?? 50), ct)).ToHttpResult();

    /// <summary>Rows go out as they are read (flushed every few hundred), with a UTF-8 BOM so spreadsheet apps show Vietnamese.</summary>
    private static async Task<IResult> ExportAsync(Guid id, ClaimsPrincipal user, ExportAttendeesHandler handler, CancellationToken ct)
    {
        var rows = await handler.HandleAsync(user.UserId(), id, ct);
        if (rows.IsFailure)
        {
            return rows.ToHttpResult();
        }

        return Results.Stream(
            async body =>
            {
                await using var writer = new StreamWriter(body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), bufferSize: 16 * 1024);
                await writer.WriteLineAsync(Line(Header));
                var count = 0;
                await foreach (var row in rows.Value.WithCancellation(ct))
                {
                    await writer.WriteLineAsync(Line(Fields(row)));
                    if (++count % 500 == 0)
                    {
                        await writer.FlushAsync(ct);
                    }
                }
            },
            "text/csv; charset=utf-8",
            $"attendees-{id:N}.csv");
    }

    private static string?[] Fields(AttendeeRow row) =>
    [
        row.OrderId.ToString(),
        row.BuyerName,
        row.BuyerEmail,
        row.HolderName,
        row.TierName,
        row.Section,
        row.Row,
        row.Number,
        row.PaidAt?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        row.CheckedIn ? "Đã check-in" : "Chưa check-in",
        row.CheckedInAt?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture),
    ];

    private static string Line(IEnumerable<string?> fields) => string.Join(',', fields.Select(Escape));

    /// <summary>RFC 4180 quoting, and a leading apostrophe on values a spreadsheet would run as a formula (CSV injection).</summary>
    internal static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : value;
    }
}
