# Task B1 report: Domain Order / PaymentAttempt / Ticket / state machine

Status: DONE. Tests: Domain.Tests 228 pass, ArchitectureTests 15 pass. Build in-repo worked (no mirror needed).

## Files
src/TicketRush.Domain/Orders: Order.cs, OrderStatus.cs, OrderItem.cs, BuyerInfo.cs, OrderDomainEvents.cs, RefundReasons.cs, OrderValidator.cs (internal), PaymentAttempt.cs, PaymentAttemptStatus.cs
src/TicketRush.Domain/Tickets: Ticket.cs, TicketStatus.cs
tests/TicketRush.Domain.Tests/Orders: OrderTestData.cs, OrderTransitionTests.cs (8 methods x 8 states = 64 cases), OrderTests.cs, PaymentAttemptTests.cs; Tickets/TicketTests.cs (4 methods x 4 states)

## Public API
- `enum OrderStatus { Pending, AwaitingPayment, PaymentFailed, Paid, Cancelled, Expired, RefundPending, Refunded }`
- `sealed record OrderItem(Guid TierId, int? SeatIndex, Money UnitPrice, int Quantity)` + `Money LineTotal()` (method, not property, so JSON/EF skip it)
- `sealed record BuyerInfo(string Name, string Email, string? Phone)` (name/email trimmed on Create)
- `sealed class Order : AggregateRoot, IAuditable` (CreatedAt/UpdatedAt stamped by the interceptor)
  - props (private set): `UserId, EventId, HoldId, Items (IReadOnlyList<OrderItem>, backing field _items), Total (Money), Buyer, Status, ExpiresAt, PaidAt?, RefundReason?, CreatedAt, UpdatedAt`; computed `IsTerminalForPayment` (Paid/Refunded/Cancelled), `IsOpen` (Pending/AwaitingPayment/PaymentFailed)
  - `static Order Create(Guid userId, Guid eventId, Guid holdId, IReadOnlyList<OrderItem> items, Money total, BuyerInfo buyer, DateTimeOffset expiresAt, DateTimeOffset now)` -> Pending, no events
  - `StartPayment(DateTimeOffset now)`: Pending|PaymentFailed -> AwaitingPayment; throws `order.expired` if now >= ExpiresAt (when state would otherwise allow it)
  - `MarkPaid(DateTimeOffset now)`: AwaitingPayment|PaymentFailed|Expired -> Paid, sets PaidAt, raises OrderPaid
  - `MarkPaymentFailed()`: AwaitingPayment -> PaymentFailed (no event)
  - `Cancel()`: Pending -> Cancelled, raises OrderCancelled
  - `Expire()`: Pending|AwaitingPayment|PaymentFailed -> Expired, raises OrderExpired
  - `MarkUnfulfillable()`: Expired|AwaitingPayment|PaymentFailed -> RefundPending, RefundReason = `seats_unavailable`, raises RefundRequested
  - `RequestRefundForCancelledEvent()`: Paid -> RefundPending, RefundReason = `event_cancelled`, raises RefundRequested
  - `MarkRefunded()`: RefundPending -> Refunded, raises OrderRefunded
  - `IReadOnlyList<Ticket> IssueTickets()`: one Valid ticket per seat / per GA unit; legal exactly where MarkPaid is (for the inventory strategy to insert before MarkPaid)
  - Illegal: `DomainException` code `order.illegal_transition`, state and events untouched. Create codes: `order.invalid_reference`, `order.invalid_expiry`, `order.invalid_buyer`, `order.no_items`, `order.invalid_item`, `order.total_mismatch`, plus `money.currency_mismatch` from Money.
- `static class RefundReasons { SeatsUnavailable = "seats_unavailable"; EventCancelled = "event_cancelled" }`
- Events (all `sealed record ... : IDomainEvent`, JSON round-trip tested; registered automatically, see below):
  - `OrderPaid(Guid OrderId, Guid UserId, Guid EventId, Guid HoldId, long TotalAmount, string Currency, int TicketCount, DateTimeOffset PaidAt)`
  - `OrderCancelled(Guid OrderId, Guid UserId, Guid EventId, Guid HoldId)`
  - `OrderExpired(Guid OrderId, Guid UserId, Guid EventId, Guid HoldId)`
  - `RefundRequested(Guid OrderId, Guid UserId, Guid EventId, long Amount, string Currency, string Reason)`
  - `OrderRefunded(Guid OrderId, Guid UserId, long Amount, string Currency)`
- `enum PaymentAttemptStatus { Pending, Succeeded, Failed, Expired }`
- `sealed class PaymentAttempt : AggregateRoot, IAuditable`: `OrderId, Provider, ProviderSessionId, Amount (Money), Status, ExpiresAt, CreatedAt, UpdatedAt`; `static readonly TimeSpan GatewayMargin = 30 s`
  - `static Create(Guid orderId, string provider, string providerSessionId, Money amount, DateTimeOffset orderExpiresAt, DateTimeOffset now)`: ExpiresAt = orderExpiresAt - 30 s; throws `payment.invalid_attempt` / `payment.window_closed` (ExpiresAt <= now)
  - `bool IsLive(now)` (Pending && now < ExpiresAt), `MarkSucceeded()` (from Pending|Failed|Expired), `MarkFailed()`, `MarkExpired()` (Pending only); illegal -> `payment.illegal_transition`
- `enum TicketStatus { Valid, CheckedIn, Listed, Voided }`
- `sealed class Ticket : AggregateRoot, IAuditable`: `OrderId, EventId, TierId, SeatIndex?, OwnerUserId, Status, QrSecret (byte[32]), CreatedAt, UpdatedAt`; `const int QrSecretLength = 32`
  - `static Issue(orderId, eventId, tierId, int? seatIndex, ownerUserId)` (secret = RandomNumberGenerator.GetBytes(32)); `CheckIn()` Valid->CheckedIn; `List()` Valid->Listed; `Unlist()` Listed->Valid; `Void()` Valid|Listed->Voided; illegal -> `ticket.illegal_transition`

## Decisions
- State machine = one private transition table (`FrozenDictionary<Trigger, Transition>`) in Order, not State-pattern classes: every method is 1-6 lines. Exhaustive test mirrors the table independently.
- Late-webhook policy (documented in Order XML docs): the handler checks state first. Domain throws for MarkPaymentFailed on Paid and MarkPaid on Paid (also on RefundPending/Refunded/Cancelled). Use `order.IsTerminalForPayment` to skip (RefundPending is not in it, check `Status` too if a late Succeeded may arrive there).
- MarkPaid legal from AwaitingPayment, PaymentFailed (out-of-order Failed then Succeeded), Expired (late success, seats free). MarkUnfulfillable from Expired/AwaitingPayment/PaymentFailed per brief.
- Cancel is Pending only (strict reading of the diagram). A PaymentFailed order waits for expiry or retries; widen the table row if the product wants cancel there.
- Expire legal from all three open states (spec: OrderExpiryService expires "open orders"), though the diagram draws it from Pending only.
- `Total` is passed to Create and validated against sum of lines (hold-copied price), not computed.
- Seated line must have Quantity 1; mixed currency raises `money.currency_mismatch`.
- StartPayment takes `now` (not in the brief) so a Pending order whose hold already died is refused with `order.expired`.
- Extra events beyond the brief: OrderCancelled (release the hold), OrderRefunded (notification). OrderExpired/OrderCancelled carry HoldId.
- Ticket List/Unlist/CheckIn exist only so the Voided-from-Listed/CheckedIn rules are real and tested; spec 10 may extend.
- QrSecret is plain bytes in the domain (`byte[]`, CA1819 suppressed). Encryption at rest with Data Protection is infrastructure's job (EF value converter in B2).
- Event registration: `DomainEventTypeRegistry` scans every concrete `IDomainEvent` in the Domain assembly by simple name, so no registry edit was needed; the five new events are picked up (names must stay unique across namespaces: they are).
- Order/PaymentAttempt/Ticket implement `IAuditable` and all are `AggregateRoot`s (own xmin concurrency in B2). Private parameterless ctors exist for EF; no EF config or migrations added.

## Commands
- Red: `dotnet build tests/TicketRush.Domain.Tests` failed with missing Orders namespace before implementation.
- `dotnet test tests/TicketRush.Domain.Tests` -> 228 passed, 0 failed
- `dotnet test tests/TicketRush.ArchitectureTests` -> 15 passed, 0 failed
