-- Releases a hold (user DELETE, expiry sweep, cancellation). Idempotent (spec 07).
-- KEYS: 1 held (HASH seat -> holdId), 2 expiry (ZSET), 3 userhold (STRING), 4 version (STRING), 5 gaheld (HASH),
--       6 hold (HASH), 7.. GA remaining of every GA tier of the event, in the order of ARGV[3]
-- ARGV: 1 holdId, 2 delta channel, 3 GA tier ids csv
-- Returns {'OK', version} | {'NOT_FOUND'}.
-- Seats are freed only where they still belong to this hold, so a late release never frees another buyer's seat.

local function split(s)
  local out = {}
  if s == nil or s == '' then return out end
  for part in string.gmatch(s, '[^,]+') do out[#out + 1] = part end
  return out
end

local holdId = ARGV[1]
local hold = redis.call('HMGET', KEYS[6], 'seats', 'ga', 'userId')
if not hold[3] then
  redis.call('ZREM', KEYS[2], holdId)
  return {'NOT_FOUND'}
end

local gaKey = {}
for i, tier in ipairs(split(ARGV[3])) do gaKey[tier] = KEYS[6 + i] end

local freed = {}
for _, seat in ipairs(split(hold[1])) do
  if redis.call('HGET', KEYS[1], seat) == holdId then
    redis.call('HDEL', KEYS[1], seat)
    freed[#freed + 1] = seat
  end
end

local tiers = {}
for _, part in ipairs(split(hold[2])) do
  local tier, qty = string.match(part, '([^:]+):(%d+)')
  if gaKey[tier] then
    local remaining = redis.call('INCRBY', gaKey[tier], qty)
    redis.call('HINCRBY', KEYS[5], tier, -tonumber(qty))
    tiers[#tiers + 1] = '"' .. tier .. '":' .. remaining
  end
end

redis.call('DEL', KEYS[6])
redis.call('ZREM', KEYS[2], holdId)
if redis.call('GET', KEYS[3]) == holdId then redis.call('DEL', KEYS[3]) end

local version = redis.call('INCR', KEYS[4])
redis.call('PUBLISH', ARGV[2], '{"v":' .. version .. ',"held":[],"freed":[' .. table.concat(freed, ',')
  .. '],"sold":[],"ga":{' .. table.concat(tiers, ',') .. '}}')
return {'OK', version}
