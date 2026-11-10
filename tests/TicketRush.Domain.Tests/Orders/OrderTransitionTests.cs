using TicketRush.Domain.Orders;
using TicketRush.Domain.Primitives;
using static TicketRush.Domain.Tests.Orders.OrderTestData;

namespace TicketRush.Domain.Tests.Orders;

/// <summary>Exhaustive state x method table for the order state machine (spec 08).</summary>
public sealed class OrderTransitionTests
{
    private static readonly OrderStatus[] AllStatuses = Enum.GetValues<OrderStatus>();

    private static readonly Dictionary<string, Action<Order>> Methods = new()
    {
        [nameof(Order.StartPayment)] = o => o.StartPayment(Now),
        [nameof(Order.MarkPaid)] = o => o.MarkPaid(Now.AddMinutes(1)),
        [nameof(Order.MarkPaymentFailed)] = o => o.MarkPaymentFailed(),
        [nameof(Order.Cancel)] = o => o.Cancel(),
        [nameof(Order.Expire)] = o => o.Expire(),
        [nameof(Order.MarkUnfulfillable)] = o => o.MarkUnfulfillable(),
        [nameof(Order.RequestRefundForCancelledEvent)] = o => o.RequestRefundForCancelledEvent(),
        [nameof(Order.MarkRefunded)] = o => o.MarkRefunded(),
    };

    /// <summary>method -> (from status -> resulting status, event type raised or null). Anything absent is illegal.</summary>
    private static readonly Dictionary<string, Dictionary<OrderStatus, (OrderStatus To, Type? Event)>> Legal = new()
    {
        [nameof(Order.StartPayment)] = new()
        {
            [OrderStatus.Pending] = (OrderStatus.AwaitingPayment, null),
            [OrderStatus.PaymentFailed] = (OrderStatus.AwaitingPayment, null),
        },
        [nameof(Order.MarkPaid)] = new()
        {
            [OrderStatus.AwaitingPayment] = (OrderStatus.Paid, typeof(OrderPaid)),
            [OrderStatus.PaymentFailed] = (OrderStatus.Paid, typeof(OrderPaid)),
            [OrderStatus.Expired] = (OrderStatus.Paid, typeof(OrderPaid)),
        },
        [nameof(Order.MarkPaymentFailed)] = new()
        {
            [OrderStatus.AwaitingPayment] = (OrderStatus.PaymentFailed, null),
        },
        [nameof(Order.Cancel)] = new()
        {
            [OrderStatus.Pending] = (OrderStatus.Cancelled, typeof(OrderCancelled)),
        },
        [nameof(Order.Expire)] = new()
        {
            [OrderStatus.Pending] = (OrderStatus.Expired, typeof(OrderExpired)),
            [OrderStatus.AwaitingPayment] = (OrderStatus.Expired, typeof(OrderExpired)),
            [OrderStatus.PaymentFailed] = (OrderStatus.Expired, typeof(OrderExpired)),
        },
        [nameof(Order.MarkUnfulfillable)] = new()
        {
            [OrderStatus.Expired] = (OrderStatus.RefundPending, typeof(RefundRequested)),
            [OrderStatus.AwaitingPayment] = (OrderStatus.RefundPending, typeof(RefundRequested)),
            [OrderStatus.PaymentFailed] = (OrderStatus.RefundPending, typeof(RefundRequested)),
        },
        [nameof(Order.RequestRefundForCancelledEvent)] = new()
        {
            [OrderStatus.Paid] = (OrderStatus.RefundPending, typeof(RefundRequested)),
        },
        [nameof(Order.MarkRefunded)] = new()
        {
            [OrderStatus.RefundPending] = (OrderStatus.Refunded, typeof(OrderRefunded)),
        },
    };

    public static TheoryData<string, OrderStatus> Matrix()
    {
        var data = new TheoryData<string, OrderStatus>();
        foreach (var method in Methods.Keys)
        {
            foreach (var status in AllStatuses)
            {
                data.Add(method, status);
            }
        }

        return data;
    }

    [Fact]
    public void The_matrix_covers_every_status_and_every_public_transition_method()
    {
        Legal.Keys.ShouldBe(Methods.Keys, ignoreOrder: true);
        AllStatuses.Length.ShouldBe(8);
        Matrix().Count.ShouldBe(Methods.Count * AllStatuses.Length);
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Every_status_and_method_pair_behaves_per_the_table(string method, OrderStatus from)
    {
        var order = InStatus(from);

        if (Legal[method].TryGetValue(from, out var expected))
        {
            Methods[method](order);

            order.Status.ShouldBe(expected.To);
            var events = order.DequeueDomainEvents();
            if (expected.Event is null)
            {
                events.ShouldBeEmpty();
            }
            else
            {
                events.Count.ShouldBe(1);
                events[0].GetType().ShouldBe(expected.Event);
            }
        }
        else
        {
            var ex = Should.Throw<DomainException>(() => Methods[method](order));

            ex.Code.ShouldBe("order.illegal_transition");
            order.Status.ShouldBe(from);
            order.DomainEvents.ShouldBeEmpty();
        }
    }
}
