-- Marks a paid order's seats sold, after checkout committed it to the database (spec 07/08). Idempotent per order.
-- KEYS: 1 sold (SET), 2 held (HASH), 3 expiry (ZSET), 4 userhold (STRING), 5 bought (STRING), 6 version (STRING),
--       7 gaheld (HASH), 8 hold (HASH), 9 commits (STRING), 10 committed marker of the order (STRING, same hash tag),
--       11.. GA remaining of every GA tier, in the order of ARGV[6]
-- ARGV: 1 holdId, 2 userId, 3 seats csv (sorted), 4 ga csv "tier:qty,...", 5 delta channel, 6 GA tier ids csv
-- Returns {'OK', version} | {'DUPLICATE', version} (the order was already committed: nothing changed, nothing published).
-- The marker is SET NX with a 7 day TTL, so a redelivered commit never counts bought / sold / GA twice.
-- Live hold (its hash still exists): its GA was already taken when it was created, so only gaheld moves. A hold that is
-- not live (expired and swept, released, or replaced by a newer hold of the user): gaheld is not touched (it belongs to
-- whatever hold holds the GA now) and this order's GA is taken from remaining, exactly once (late payment, C3). The
-- database's unique index (08) is what settles a seat that was re-held by someone else in the meantime.

local function split(s)
  local out = {}
  if s == nil or s == '' then return out end
  for part in string.gmatch(s, '[^,]+') do out[#out + 1] = part end
  return out
end

-- 7 days: longer than any retry window of the payment gateway webhook.
if not redis.call('SET', KEYS[10], '1', 'NX', 'EX', 604800) then
  return {'DUPLICATE', tonumber(redis.call('GET', KEYS[6]) or '0')}
end

local holdId = ARGV[1]
local gaKey = {}
for i, tier in ipairs(split(ARGV[6])) do gaKey[tier] = KEYS[10 + i] end
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
-- Tells a reconcile that read the database before this commit that its picture is stale.
redis.call('INCR', KEYS[9])

local version = redis.call('INCR', KEYS[6])
redis.call('PUBLISH', ARGV[5], '{"v":' .. version .. ',"held":[],"freed":[],"sold":[' .. table.concat(seats, ',')
  .. '],"ga":{' .. table.concat(tiers, ',') .. '}}')
return {'OK', version}
