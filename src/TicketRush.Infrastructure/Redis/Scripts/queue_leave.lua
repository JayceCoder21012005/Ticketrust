-- Removes a user from the waiting room and frees their active slot.
-- KEYS: 1 pre (SET), 2 line (HASH), 3 active (ZSET), 4 tokens (HASH), 5 done (SET), 6 opening (SET), 7 state (HASH)
-- ARGV: 1 userId
-- Users who had a number (or an admission) are marked done: they can only come back at the tail.
local user = ARGV[1]
redis.call('SREM', KEYS[1], user)
redis.call('SREM', KEYS[6], user)
local had = redis.call('HEXISTS', KEYS[2], user) == 1 or redis.call('ZSCORE', KEYS[3], user) ~= false
redis.call('ZREM', KEYS[3], user)
redis.call('HDEL', KEYS[4], user)
if had then
  redis.call('SADD', KEYS[5], user)
  local closes = tonumber(redis.call('HGET', KEYS[7], 'closesAt') or '0')
  local clock = redis.call('TIME')
  local nowMs = tonumber(clock[1]) * 1000 + math.floor(tonumber(clock[2]) / 1000)
  redis.call('PEXPIREAT', KEYS[5], math.max(closes + 86400000, nowMs + 3600000))
end
return 1
