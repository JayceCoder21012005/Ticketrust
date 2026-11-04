-- Gives the next contiguous ticket numbers to frozen pre-queue members, in the order supplied (already shuffled).
-- Atomic per batch, so a leader that dies mid-opening leaves no gaps or duplicates; a successor resumes with
-- whoever is still in `opening`. Members who left meanwhile (no longer in `opening`) are skipped.
-- KEYS: 1 opening (SET), 2 line (HASH), 3 seq (STRING), 4 state (HASH)
-- ARGV: userIds
-- Returns how many numbers were assigned.
local closes = tonumber(redis.call('HGET', KEYS[4], 'closesAt') or '0')
local clock = redis.call('TIME')
local nowMs = tonumber(clock[1]) * 1000 + math.floor(tonumber(clock[2]) / 1000)
local expireAt = math.max(closes + 86400000, nowMs + 3600000)

local assigned = 0
for i = 1, #ARGV do
  local user = ARGV[i]
  if redis.call('SREM', KEYS[1], user) == 1 and redis.call('HEXISTS', KEYS[2], user) == 0 then
    local n = redis.call('INCR', KEYS[3])
    redis.call('HSET', KEYS[2], user, n)
    assigned = assigned + 1
  end
end
if assigned > 0 then
  redis.call('PEXPIREAT', KEYS[2], expireAt)
  redis.call('PEXPIREAT', KEYS[3], expireAt)
end
return assigned
