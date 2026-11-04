-- Moves a sale from Pre to Opening and freezes the pre-queue. Also accepts Opening, so a new leader can resume an
-- interrupted opening.
-- KEYS: 1 state (HASH), 2 pre (SET), 3 opening (SET)
-- At T0 the pre-queue is renamed to `opening` in one step. Everyone in `opening` is shuffled together; anyone who joins
-- afterwards lands in a fresh `pre` and is numbered after all of them, however long the shuffle or a failover takes.
-- Returns 1 when the caller should run (or resume) the shuffle, 0 when there is nothing to open.
local status = redis.call('HGET', KEYS[1], 'status')
if status == 'Pre' then
  redis.call('HSET', KEYS[1], 'status', 'Opening')
  if redis.call('EXISTS', KEYS[2]) == 1 then
    redis.call('RENAME', KEYS[2], KEYS[3])
    local closes = tonumber(redis.call('HGET', KEYS[1], 'closesAt') or '0')
    local clock = redis.call('TIME')
    local nowMs = tonumber(clock[1]) * 1000 + math.floor(tonumber(clock[2]) / 1000)
    redis.call('PEXPIREAT', KEYS[3], math.max(closes + 86400000, nowMs + 3600000))
  end
  return 1
end
if status == 'Opening' then
  return 1
end
return 0
