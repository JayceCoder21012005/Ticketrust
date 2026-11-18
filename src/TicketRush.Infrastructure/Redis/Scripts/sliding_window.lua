-- Sliding-window rate limit (spec 12): one ZSET per (policy, partition), one member per accepted request, scored by Redis time.
-- KEYS: 1 window (ZSET)
-- ARGV: 1 limit, 2 windowMs, 3 member (unique per request)
-- Returns: { 1, 0 } when the request is accepted, { 0, retryAfterMs } when the window is full (a refused request is not recorded,
-- so hammering a full window never extends it). Time comes from Redis, so instances with skewed clocks agree.
local t = redis.call('TIME')
local now = tonumber(t[1]) * 1000 + math.floor(tonumber(t[2]) / 1000)
local windowMs = tonumber(ARGV[2])
redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', now - windowMs)
if redis.call('ZCARD', KEYS[1]) >= tonumber(ARGV[1]) then
  local oldest = redis.call('ZRANGE', KEYS[1], 0, 0, 'WITHSCORES')
  local retry = tonumber(oldest[2]) + windowMs - now
  if retry < 1 then retry = 1 end
  return { 0, retry }
end
redis.call('ZADD', KEYS[1], now, ARGV[3])
redis.call('PEXPIRE', KEYS[1], windowMs)
return { 1, 0 }
