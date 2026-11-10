-- Creates or replaces a user's hold on seats and GA quantities, all or nothing (spec 07, C1).
-- KEYS: 1 sold (SET), 2 held (HASH seat -> holdId), 3 expiry (ZSET holdId -> ms), 4 userhold (STRING), 5 bought (STRING),
--       6 version (STRING), 7 gaheld (HASH tier -> qty), 8 new hold (HASH), 9 old hold (HASH; any unused key when none),
--       10.. GA remaining of every GA tier of the event (STRING), in the order of ARGV[11]
-- ARGV: 1 holdId, 2 userId, 3 seats csv (sorted, C9), 4 ga csv "tier:qty,...", 5 max tickets, 6 replace (0/1),
--       7 now ms, 8 ttl ms, 9 holdId the caller read from userhold ('' for none), 10 delta channel, 11 GA tier ids csv
-- Returns {'OK', expiresAtMs, version} | {'SEAT_UNAVAILABLE', seat...} | {'GA_INSUFFICIENT', tierId}
--       | {'LIMIT_EXCEEDED'} | {'HAS_HOLD'} | {'STALE'} (userhold changed since the caller read it: nothing written, retry).
-- A replaced hold's seats stay held the whole time: seats in both holds only change owner, so nobody sees them free.
-- A dropped seat that is already sold is cleared from held but never announced as freed.
-- An expired hold of the same user that the sweeper has not released yet counts as replaceable.

local function split(s)
  local out = {}
  if s == nil or s == '' then return out end
  for part in string.gmatch(s, '[^,]+') do out[#out + 1] = part end
  return out
end

local function parseGa(s)
  local out = {}
  for _, part in ipairs(split(s)) do
    local tier, qty = string.match(part, '([^:]+):(%d+)')
    out[tier] = (out[tier] or 0) + tonumber(qty)
  end
  return out
end

local function toSet(list)
  local set = {}
  for _, v in ipairs(list) do set[v] = true end
  return set
end

local holdId, userId = ARGV[1], ARGV[2]
local seats = split(ARGV[3])
local ga = parseGa(ARGV[4])
local maxTickets = tonumber(ARGV[5])
local replace = ARGV[6] == '1'
local now, ttl = tonumber(ARGV[7]), tonumber(ARGV[8])
local tierIds = split(ARGV[11])
local gaKey = {}
for i, tier in ipairs(tierIds) do gaKey[tier] = KEYS[9 + i] end

local current = redis.call('GET', KEYS[4]) or ''
if current ~= ARGV[9] then return {'STALE'} end

local oldId, oldSeats, oldGa = nil, {}, {}
if current ~= '' and redis.call('EXISTS', KEYS[9]) == 1 then
  local old = redis.call('HMGET', KEYS[9], 'seats', 'ga', 'expiresAt')
  local expired = tonumber(old[3] or '0') <= now
  if not replace and not expired then return {'HAS_HOLD'} end
  oldId, oldSeats, oldGa = current, split(old[1]), parseGa(old[2])
end

local requested = #seats
for _, qty in pairs(ga) do requested = requested + qty end
if tonumber(redis.call('GET', KEYS[5]) or '0') + requested > maxTickets then return {'LIMIT_EXCEEDED'} end

local unavailable = {}
for _, seat in ipairs(seats) do
  local owner = redis.call('HGET', KEYS[2], seat)
  if redis.call('SISMEMBER', KEYS[1], seat) == 1 or (owner and owner ~= oldId) then
    unavailable[#unavailable + 1] = seat
  end
end
if #unavailable > 0 then
  table.insert(unavailable, 1, 'SEAT_UNAVAILABLE')
  return unavailable
end

for tier, qty in pairs(ga) do
  if gaKey[tier] == nil then return {'GA_INSUFFICIENT', tier} end
  local remaining = tonumber(redis.call('GET', gaKey[tier]) or '0') + (oldGa[tier] or 0)
  if remaining < qty then return {'GA_INSUFFICIENT', tier} end
end

-- Every check passed: write.
local oldSet, newSet = toSet(oldSeats), toSet(seats)
local heldDelta, freed = {}, {}
for _, seat in ipairs(seats) do
  redis.call('HSET', KEYS[2], seat, holdId)
  if not oldSet[seat] then heldDelta[#heldDelta + 1] = seat end
end
for _, seat in ipairs(oldSeats) do
  if not newSet[seat] and redis.call('HGET', KEYS[2], seat) == oldId then
    redis.call('HDEL', KEYS[2], seat)
    if redis.call('SISMEMBER', KEYS[1], seat) == 0 then freed[#freed + 1] = seat end
  end
end

local tiers = {}
for _, tier in ipairs(tierIds) do
  local change = (ga[tier] or 0) - (oldGa[tier] or 0)
  if change ~= 0 then
    local remaining = redis.call('DECRBY', gaKey[tier], change)
    redis.call('HINCRBY', KEYS[7], tier, change)
    tiers[#tiers + 1] = '"' .. tier .. '":' .. remaining
  end
end

if oldId then
  redis.call('DEL', KEYS[9])
  redis.call('ZREM', KEYS[3], oldId)
end

local expiresAt = now + ttl
redis.call('HSET', KEYS[8], 'userId', userId, 'seats', ARGV[3], 'ga', ARGV[4], 'expiresAt', expiresAt)
redis.call('ZADD', KEYS[3], expiresAt, holdId)
-- The sweeper clears userhold on release; the PX only cleans up after a lost release.
redis.call('SET', KEYS[4], holdId, 'PX', ttl + 300000)

local version = redis.call('INCR', KEYS[6])
redis.call('PUBLISH', ARGV[10], '{"v":' .. version .. ',"held":[' .. table.concat(heldDelta, ',') .. '],"freed":['
  .. table.concat(freed, ',') .. '],"sold":[],"ga":{' .. table.concat(tiers, ',') .. '}}')
return {'OK', tostring(expiresAt), version}
