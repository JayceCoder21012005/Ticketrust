-- Gate scan guard (spec 11): count one scan against the staff member's per-second limit and fetch the remembered answer
-- of the same payload, in one round trip.
-- KEYS: 1 rate counter (STRING, tr:{env}:scan-rate:{staffId}), 2 repeat memory (STRING, tr:{env}:scan-repeat:{staffId}:{payloadHash})
-- ARGV: 1 limit, 2 windowMs
-- Returns: { allowed (1|0), ttlMs, previous (string or false) }
-- The window starts at the first scan and never slides. A refused scan does not read the memory.
local count = redis.call('INCR', KEYS[1])
local ttl = redis.call('PTTL', KEYS[1])
if count == 1 or ttl < 0 then
  redis.call('PEXPIRE', KEYS[1], tonumber(ARGV[2]))
  ttl = tonumber(ARGV[2])
end
if count > tonumber(ARGV[1]) then
  return { 0, ttl, false }
end
return { 1, ttl, redis.call('GET', KEYS[2]) }
