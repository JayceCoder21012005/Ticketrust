using TicketRush.Api.Common.RateLimiting;
using System.Security.Claims;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using TicketRush.Api.Common.Auth;
using TicketRush.Api.Common.Http;
using TicketRush.Api.Common.Idempotency;
using TicketRush.Application.Features.Orders;
using TicketRush.Application.Options;

namespace TicketRush.Api.Features.Orders;

public sealed record BuyerRequest(string Name, string Email, string? Phone);

/// <param name="HoldId">The buyer's live hold for <paramref name="EventId"/>.</param>
public sealed record CreateOrderRequest(Guid EventId, Guid HoldId, BuyerRequest Buyer);

/// <summary>Query string of <c>GET /me/orders</c>.</summary>
public sealed record ListMyOrdersRequest(
    [property: FromQuery(Name = "cursor")] string? Cursor,
    [property: FromQuery(Name = "limit")] int? Limit);

/// <summary>Orders (spec 08): creating one from a hold, reading, paying and cancelling it, and the buyer's list.</summary>
public static class OrderEndpoints
{
    public static RouteGroupBuilder MapOrderEndpoints(this RouteGroupBuilder v1)
    {
        // A replayed answer outlives the hold it was made for only as a stale "Pending"; after the hold's life a retry should see the real state.
        var holdTtl = v1.ServiceProvider().GetRequiredService<IOptions<HoldsOptions>>().Value.Ttl;
        var orders = v1.MapGroup("/orders").WithTags("Orders").RequireAuthorization();

        orders.MapPost("/", CreateAsync)
            .RequireRateLimit(RateLimitPolicies.Checkout)
            .RequireIdempotencyKey(holdTtl)
            .WithName("CreateOrder")
            .Produces<OrderResponse>(StatusCodes.Status201Created)
            .Produces<OrderResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        orders.MapGet("/{id:guid}", GetAsync)
            .WithName("GetOrder")
            .Produces<OrderResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);
        // The answer is a redirect into a gateway session that lives as long as the hold, so a replay is useful for that long only.
        orders.MapPost("/{id:guid}/pay", PayAsync)
            .RequireRateLimit(RateLimitPolicies.Checkout)
            .RequireIdempotencyKey(holdTtl)
            .WithName("PayOrder")
            .Produces<PayOrderResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        // Spec 18: the result page asks this on arrival; the server asks the gateway (never trusts the redirect's query string).
        orders.MapPost("/{id:guid}/payment/confirm", ConfirmPaymentAsync)
            .RequireRateLimit(RateLimitPolicies.Checkout)
            .WithName("ConfirmOrderPayment")
            .Produces<OrderResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);
        orders.MapPost("/{id:guid}/cancel", CancelAsync)
            .RequireIdempotencyKey()
            .WithName("CancelOrder")
            .Produces<OrderResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        v1.MapGroup("/me/orders").WithTags("Orders").RequireAuthorization()
            .MapGet("/", ListMineAsync)
            .WithName("ListMyOrders")
            .Produces<OrderPage>()
            .ProducesValidationProblem();
        return v1;
    }

    private static async Task<IResult> CreateAsync(
        CreateOrderRequest request,
        ClaimsPrincipal user,
        IValidator<CreateOrderCommand> validator,
        CreateOrderHandler handler,
        CancellationToken ct)
    {
        var buyer = request.Buyer ?? new BuyerRequest(string.Empty, string.Empty, null);
        var command = new CreateOrderCommand(user.UserId(), request.EventId, request.HoldId, new BuyerInput(buyer.Name ?? string.Empty, buyer.Email ?? string.Empty, buyer.Phone));
        if (await validator.ValidateToProblemAsync(command, ct) is { } invalid)
        {
            return invalid;
        }

        return (await handler.HandleAsync(command, ct)).ToHttpResult(created => created.Created
            ? TypedResults.Created($"/api/v1/orders/{created.Order.Id}", created.Order)
            : TypedResults.Ok(created.Order));
    }

    private static async Task<IResult> GetAsync(Guid id, ClaimsPrincipal user, GetOrderHandler handler, CancellationToken ct) =>
        (await handler.HandleAsync(user.UserId(), id, ct)).ToHttpResult();

    /// <remarks>No request body is read: where the gateway sends the buyer back to is decided by the server, never by the client.</remarks>
    private static async Task<IResult> PayAsync(Guid id, ClaimsPrincipal user, PayOrderHandler handler, CancellationToken ct) =>
        (await handler.HandleAsync(user.UserId(), id, ct)).ToHttpResult();

    private static async Task<IResult> ConfirmPaymentAsync(Guid id, ClaimsPrincipal user, ConfirmPaymentHandler handler, CancellationToken ct) =>
        (await handler.HandleAsync(user.UserId(), id, ct)).ToHttpResult();

    private static async Task<IResult> CancelAsync(Guid id, ClaimsPrincipal user, CancelOrderHandler handler, CancellationToken ct) =>
        (await handler.HandleAsync(user.UserId(), id, ct)).ToHttpResult();

    private static async Task<IResult> ListMineAsync(
        [AsParameters] ListMyOrdersRequest request,
        ClaimsPrincipal user,
        IValidator<ListMyOrdersQuery> validator,
        ListMyOrdersHandler handler,
        CancellationToken ct)
    {
        var query = new ListMyOrdersQuery(user.UserId(), request.Cursor, request.Limit ?? ListMyOrdersQuery.DefaultLimit);
        return await validator.ValidateToProblemAsync(query, ct) ?? TypedResults.Ok(await handler.HandleAsync(query, ct));
    }

    private static IServiceProvider ServiceProvider(this IEndpointRouteBuilder builder) => builder.ServiceProvider;
}
