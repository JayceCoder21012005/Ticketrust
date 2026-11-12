-- Fixed-window request counter for the ticket qr-key endpoint (spec 10): one INCR + EXPIRE, atomically.
-- KEYS: 1 counter (STRING, one per user: tr:{env}:qr-key:{userId})
-- ARGV: 1 windowSeconds
-- Returns: { count, ttlMs } - the count after this request (the caller compares it with its limit) and the time the window has left.
-- The window starts at the first request and never slides: refused requests do not extend it. A counter without a TTL
-- (it cannot happen inside one script, but a manual SET could leave one) is given a window so it can never lock a user out for good.
local count = redis.call('INCR', KEYS[1])
local ttl = redis.call('PTTL', KEYS[1])
if count == 1 or ttl < 0 then
  redis.call('PEXPIRE', KEYS[1], tonumber(ARGV[1]) * 1000)
  ttl = tonumber(ARGV[1]) * 1000
end
return { count, ttl }
