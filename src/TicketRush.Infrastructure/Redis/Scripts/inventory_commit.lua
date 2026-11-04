-- Marks a paid order's seats sold, after checkout committed it to the database (spec 07/08).
-- KEYS: 1 sold (SET), 2 held (HASH), 3 expiry (ZSET), 4 userhold (STRING), 5 bought (STRING), 6 version (STRING),
--       7 gaheld (HASH), 8 hold (HASH), 9.. GA remaining of every GA tier of the event, in the order of ARGV[6]
-- ARGV: 1 holdId, 2 userId, 3 seats csv (sorted), 4 ga csv "tier:qty,...", 5 delta channel, 6 GA tier ids csv
-- Returns {'OK', version}.
-- Live hold: its GA was already taken when it was created, so only the held counter moves. Expired hold (late payment,
-- C3): the seats are sold anyway and GA is taken now; the database's unique index (08) is what settles a seat that was
-- re-held by someone else in the meantime.

local function split(s)
  local out = {}
  if s == nil or s == '' then return out end
  for part in string.gmatch(s, '[^,]+') do out[#out + 1] = part end
  return out
end

local holdId = ARGV[1]
local gaKey = {}
for i, tier in ipairs(split(ARGV[6])) do gaKey[tier] = KEYS[8 + i] end
local live = redis.call('EXISTS', KEYS[8]) == 1

local seats = split(ARGV[3])
local count = #seats
for _, seat in ipairs(seats) do
  redis.call('SADD', KEYS[1], seat)
  if redis.call('HGET', KEYS[2], seat) == holdId then redis.call('HDEL', KEYS[2], seat) end
end

local tiers = {}
for _, part in ipairs(split(ARGV[4])) do
  local tier, qty = string.match(part, '([^:]+):(%d+)')
  qty = tonumber(qty)
  count = count + qty
  if gaKey[tier] then
    if live then
      redis.call('HINCRBY', KEYS[7], tier, -qty)
    else
      tiers[#tiers + 1] = '"' .. tier .. '":' .. redis.call('DECRBY', gaKey[tier], qty)
    end
  end
end

if live then redis.call('DEL', KEYS[8]) end
redis.call('ZREM', KEYS[3], holdId)
if redis.call('GET', KEYS[4]) == holdId then redis.call('DEL', KEYS[4]) end
redis.call('INCRBY', KEYS[5], count)

local version = redis.call('INCR', KEYS[6])
redis.call('PUBLISH', ARGV[5], '{"v":' .. version .. ',"held":[],"freed":[],"sold":[' .. table.concat(seats, ',')
  .. '],"ga":{' .. table.concat(tiers, ',') .. '}}')
return {'OK', version}
