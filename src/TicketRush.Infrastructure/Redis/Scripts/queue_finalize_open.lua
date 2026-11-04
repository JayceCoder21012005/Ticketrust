-- Ends the opening: users who joined after T0 (the new `pre`) get numbers after everyone who was shuffled, then the
-- room opens.
-- KEYS: 1 state (HASH), 2 pre (SET), 3 line (HASH), 4 seq (STRING), 5 opening (SET)
-- Returns the count of post-T0 joiners numbered, or -1 when the room was not in Opening.
if redis.call('HGET', KEYS[1], 'status') ~= 'Opening' then
  return -1
end
local closes = tonumber(redis.call('HGET', KEYS[1], 'closesAt') or '0')
local clock = redis.call('TIME')
local nowMs = tonumber(clock[1]) * 1000 + math.floor(tonumber(clock[2]) / 1000)
local expireAt = math.max(closes + 86400000, nowMs + 3600000)

local numbered = 0
-- `opening` is normally drained by now; anyone left (a shuffle that was cut short) goes first, then post-T0 joiners.
for _, set in ipairs({KEYS[5], KEYS[2]}) do
  for _, user in ipairs(redis.call('SMEMBERS', set)) do
    if redis.call('HEXISTS', KEYS[3], user) == 0 then
      local n = redis.call('INCR', KEYS[4])
      redis.call('HSET', KEYS[3], user, n)
      numbered = numbered + 1
    end
  end
end
redis.call('DEL', KEYS[2], KEYS[5])
if numbered > 0 then
  redis.call('PEXPIREAT', KEYS[3], expireAt)
  redis.call('PEXPIREAT', KEYS[4], expireAt)
end
redis.call('HSET', KEYS[1], 'status', 'Open')
return numbered
