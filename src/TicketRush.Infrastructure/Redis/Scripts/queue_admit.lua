-- One admitter tick: drops expired shoppers, then (only while Open) raises `admitted` by the allowed step.
-- KEYS: 1 state (HASH), 2 seq (STRING), 3 admitted (STRING), 4 active (ZSET), 5 tokens (HASH), 6 done (SET)
-- ARGV: 1 now ms, 2 admission rate per second, 3 max active shoppers
-- Returns {admittedBefore, admittedAfter, seq, activeCount, status}.
-- step = min(rate, max - active); admitted never passes seq, so no number is ever skipped or invented.
-- Keys written get an expiry one day after the sale closes.
local rate = tonumber(ARGV[2])
local max = tonumber(ARGV[3])
local closes = tonumber(redis.call('HGET', KEYS[1], 'closesAt') or '0')
local expireAt = math.max(closes + 86400000, tonumber(ARGV[1]) + 3600000)

local expired = redis.call('ZRANGEBYSCORE', KEYS[4], '-inf', ARGV[1])
for _, user in ipairs(expired) do
  redis.call('ZREM', KEYS[4], user)
  redis.call('HDEL', KEYS[5], user)
  redis.call('SADD', KEYS[6], user)
end
if #expired > 0 then
  redis.call('PEXPIREAT', KEYS[6], expireAt)
end

local status = redis.call('HGET', KEYS[1], 'status') or ''
local seq = tonumber(redis.call('GET', KEYS[2]) or '0')
local admitted = tonumber(redis.call('GET', KEYS[3]) or '0')
local active = redis.call('ZCARD', KEYS[4])
if status ~= 'Open' then
  return {admitted, admitted, seq, active, status}
end

local step = math.min(rate, max - active)
if step < 0 then step = 0 end
local after = math.min(admitted + step, seq)
if after > admitted then
  redis.call('SET', KEYS[3], after)
  redis.call('PEXPIREAT', KEYS[3], expireAt)
else
  after = admitted
end
return {admitted, after, seq, active, status}
