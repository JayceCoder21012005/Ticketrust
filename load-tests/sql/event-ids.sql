-- The ids sale.js needs: EVENT_ID and GA_TIER_ID. The dev seed event "Sơn Tùng M-TP Live" has 600 seats and a GA tier.
-- Shrink the GA tier first if you want demand to exceed capacity quickly:
--   UPDATE ticket_tiers SET capacity = 60 WHERE id = '<GA_TIER_ID>' AND sold_count = 0;
-- then restart api and worker (docker compose restart api worker): their startup reconcile rebuilds the GA counters from
-- the database, and the catalog entry they cached still holds the old capacity until then.
SELECT e.id AS event_id, t.id AS tier_id, t.kind, t.name, t.capacity, t.sold_count
FROM events e
JOIN ticket_tiers t ON t.event_id = e.id
WHERE e.slug = 'son-tung-mtp-live'
ORDER BY t.kind, t.name;
