-- Joins a user to a sale's waiting room. Idempotent.
-- KEYS: 1 state (HASH), 2 pre (SET), 3 line (HASH), 4 seq (STRING), 5 done (SET), 6 opening (SET)
-- ARGV: 1 initial status (used only when the state does not exist yet), 2 opensAt ms, 3 closesAt ms, 4 userId
-- Returns {status, number}. number is -1 while the user waits in `pre`/`opening` (numbers come from the shuffle at
-- opening) or when the sale is closed.
-- Every key written gets an expiry one day after the sale closes, so finished sales do not pile up in Redis.
if redis.call('HSETNX', KEYS[1], 'status', ARGV[1]) == 1 then
  redis.call('HSET', KEYS[1], 'opensAt', ARGV[2], 'closesAt', ARGV[3])
end
local status = redis.call('HGET', KEYS[1], 'status')
local user = ARGV[4]

local closes = tonumber(redis.call('HGET', KEYS[1], 'closesAt') or '0')
local clock = redis.call('TIME')
local nowMs = tonumber(clock[1]) * 1000 + math.floor(tonumber(clock[2]) / 1000)
local expireAt = math.max(closes + 86400000, nowMs + 3600000)
local function keep(key) redis.call('PEXPIREAT', key, expireAt) end
keep(KEYS[1])

-- A user who left or expired starts over at the tail with a brand new number.
if redis.call('SISMEMBER', KEYS[5], user) == 1 then
  redis.call('SREM', KEYS[5], user)
  redis.call('HDEL', KEYS[3], user)
end

local number = redis.call('HGET', KEYS[3], user)
if number then
  return {status, tonumber(number)}
end
if redis.call('SISMEMBER', KEYS[2], user) == 1 or redis.call('SISMEMBER', KEYS[6], user) == 1 then
  return {status, -1}
end

if status == 'Pre' or status == 'Opening' then
  -- During Opening this is somebody who arrived after T0: `pre` is a new set, apart from `opening` (the shuffled one).
  redis.call('SADD', KEYS[2], user)
  keep(KEYS[2])
  return {status, -1}
end
if status == 'Open' or status == 'AllHeld' then
  local n = redis.call('INCR', KEYS[4])
  redis.call('HSET', KEYS[3], user, n)
  keep(KEYS[3])
  keep(KEYS[4])
  return {status, n}
end
return {status, -1}
