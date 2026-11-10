-- Sale invariants (spec 08 acceptance #8, spec 17 "invariant checker").
--
-- Returns one row per violation; ZERO ROWS = every invariant holds. Read-only, safe to run against a live database.
-- Single source of truth: load-tests/check-sale-invariants.sh runs it with psql, and the integration test
-- SaleInvariantTests loads this very file after its concurrent mini-sale.
--
-- Column names are those of the EF mapping (snake_case): tickets, ticket_tiers, orders, order_items.
-- A ticket "counts" while its status is not 'Voided' (Valid, CheckedIn, Listed).
SELECT check_name, subject, detail
FROM (
    -- (a) A seat is sold at most once. The partial unique index ux_tickets_event_seat makes this impossible
    --     unless it was dropped or never applied; the check proves the index is doing its job.
    SELECT 'seat_sold_twice' AS check_name,
           t.event_id::text || '/seat ' || t.seat_index::text AS subject,
           count(*)::text || ' non-voided tickets' AS detail
    FROM tickets t
    WHERE t.status <> 'Voided' AND t.seat_index IS NOT NULL
    GROUP BY t.event_id, t.seat_index
    HAVING count(*) > 1

    UNION ALL

    -- (b) No tier sold beyond its capacity (seated tiers have no capacity and no sold_count).
    SELECT 'tier_oversold',
           tt.id::text,
           'sold_count ' || tt.sold_count::text || ' > capacity ' || tt.capacity::text
    FROM ticket_tiers tt
    WHERE tt.capacity IS NOT NULL AND tt.sold_count > tt.capacity

    UNION ALL

    -- (c) General admission: the counter is exactly the number of non-voided tickets of the tier.
    SELECT 'ga_sold_count_drift',
           tt.id::text,
           'sold_count ' || tt.sold_count::text || ' but ' || coalesce(live.n, 0)::text || ' non-voided tickets'
    FROM ticket_tiers tt
    LEFT JOIN (
        SELECT tier_id, count(*) AS n
        FROM tickets
        WHERE status <> 'Voided' AND seat_index IS NULL
        GROUP BY tier_id
    ) live ON live.tier_id = tt.id
    WHERE tt.kind = 'GeneralAdmission' AND tt.sold_count <> coalesce(live.n, 0)

    UNION ALL

    -- (d1) A paid order owns exactly as many non-voided tickets as it has items (seat lines count 1, GA lines their quantity).
    SELECT 'paid_order_ticket_count',
           o.id::text,
           'expected ' || i.qty::text || ' tickets, found ' || coalesce(live.n, 0)::text
    FROM orders o
    JOIN (SELECT order_id, sum(quantity) AS qty FROM order_items GROUP BY order_id) i ON i.order_id = o.id
    LEFT JOIN (
        SELECT order_id, count(*) AS n FROM tickets WHERE status <> 'Voided' GROUP BY order_id
    ) live ON live.order_id = o.id
    WHERE o.status = 'Paid' AND coalesce(live.n, 0) <> i.qty

    UNION ALL

    -- (d2) ... and the same per tier, so a ticket of the wrong tier cannot hide behind a right total.
    SELECT 'paid_order_tier_tickets',
           o.id::text || '/tier ' || i.tier_id::text,
           'expected ' || i.qty::text || ' tickets, found ' || coalesce(live.n, 0)::text
    FROM orders o
    JOIN (SELECT order_id, tier_id, sum(quantity) AS qty FROM order_items GROUP BY order_id, tier_id) i ON i.order_id = o.id
    LEFT JOIN (
        SELECT order_id, tier_id, count(*) AS n FROM tickets WHERE status <> 'Voided' GROUP BY order_id, tier_id
    ) live ON live.order_id = i.order_id AND live.tier_id = i.tier_id
    WHERE o.status = 'Paid' AND coalesce(live.n, 0) <> i.qty

    UNION ALL

    -- (d3) A ticket belongs to the event of its order.
    SELECT 'ticket_event_mismatch',
           t.id::text,
           'ticket of event ' || t.event_id::text || ' on an order of event ' || o.event_id::text
    FROM tickets t
    JOIN orders o ON o.id = t.order_id
    WHERE t.event_id <> o.event_id

    UNION ALL

    -- (d4) Only a Paid order (or one on its way back to the buyer, see e) may own non-voided tickets.
    SELECT 'unpaid_order_has_tickets',
           o.id::text,
           o.status || ' order owns ' || count(*)::text || ' non-voided tickets'
    FROM orders o
    JOIN tickets t ON t.order_id = o.id AND t.status <> 'Voided'
    WHERE o.status NOT IN ('Paid', 'RefundPending', 'Refunded')
    GROUP BY o.id, o.status

    UNION ALL

    -- (e) Money owed or returned means the seats were given back: a refund state never keeps live tickets.
    SELECT 'refund_order_has_tickets',
           o.id::text,
           o.status || ' order owns ' || count(*)::text || ' non-voided tickets'
    FROM orders o
    JOIN tickets t ON t.order_id = o.id AND t.status <> 'Voided'
    WHERE o.status IN ('RefundPending', 'Refunded')
    GROUP BY o.id, o.status
) violations
ORDER BY check_name, subject;
