-- Reads a user's state and admits them when they are due and there is room.
-- KEYS: 1 state (HASH), 2 line (HASH), 3 admitted (STRING), 4 active (ZSET), 5 tokens (HASH), 6 done (SET),
--       7 seq (STRING), 8 pre (SET), 9 opening (SET)
-- ARGV: 1 initial status, 2 opensAt ms, 3 closesAt ms, 4 userId, 5 now ms, 6 shopping window ms,
--       7 max active shoppers, 8 instant ('1' for phases without a queue: admitted as soon as they have a number)
-- Returns {status, number|-1, admitted, seq, activeCount, claimed, active, done, inPre, activeUntilMs|-1, token|''}
-- Booleans are 0/1. activeCount only counts unexpired admissions. Keys written get an expiry one day after closing.
if redis.call('HSETNX', KEYS[1], 'status', ARGV[1]) == 1 then
  redis.call('HSET', KEYS[1], 'opensAt', ARGV[2], 'closesAt', ARGV[3])
end
local status = redis.call('HGET', KEYS[1], 'status')
local user = ARGV[4]
local now = tonumber(ARGV[5])
local window = tonumber(ARGV[6])
local max = tonumber(ARGV[7])
local instant = ARGV[8] == '1'

local closes = tonumber(redis.call('HGET', KEYS[1], 'closesAt') or '0')
local expireAt = math.max(closes + 86400000, now + 3600000)
redis.call('PEXPIREAT', KEYS[1], expireAt)

-- Lazy expiry: an expired shopper never silently regains a slot just because their number is <= admitted.
local score = redis.call('ZSCORE', KEYS[4], user)
if score and tonumber(score) <= now then
  redis.call('ZREM', KEYS[4], user)
  redis.call('HDEL', KEYS[5], user)
  redis.call('SADD', KEYS[6], user)
  redis.call('PEXPIREAT', KEYS[6], expireAt)
  score = false
end

local done = redis.call('SISMEMBER', KEYS[6], user) == 1
local number = tonumber(redis.call('HGET', KEYS[2], user) or '-1')
local inPre = redis.call('SISMEMBER', KEYS[8], user) == 1 or redis.call('SISMEMBER', KEYS[9], user) == 1
local admitted = tonumber(redis.call('GET', KEYS[3]) or '0')
local seq = tonumber(redis.call('GET', KEYS[7]) or '0')
local activeCount = redis.call('ZCOUNT', KEYS[4], '(' .. ARGV[5], '+inf')

local claimed = 0
local activeUntil = -1
if score then
  activeUntil = tonumber(score)
elseif not done and number > 0 and (status == 'Open' or status == 'AllHeld')
    and (instant or number <= admitted) and activeCount < max then
  activeUntil = now + window
  redis.call('ZADD', KEYS[4], 'NX', activeUntil, user)
  redis.call('PEXPIREAT', KEYS[4], expireAt)
  claimed = 1
  activeCount = activeCount + 1
end

local active = 0
if activeUntil > 0 then active = 1 end
local doneFlag = 0
if done then doneFlag = 1 end
local preFlag = 0
if inPre then preFlag = 1 end
local token = redis.call('HGET', KEYS[5], user) or ''
return {status, number, admitted, seq, activeCount, claimed, active, doneFlag, preFlag, activeUntil, token}
