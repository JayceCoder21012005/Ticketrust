-- Waitlist hand-overs (spec 13), all or nothing. A returned ticket never passes through "free" while someone waits for it.
-- KEYS: 1 sold (SET), 2 held (HASH seat -> holdId), 3 userhold of the new holder (STRING; any unused key for 'free'),
--       4 bought of the previous owner (STRING; any unused key for 'pass'), 5 version (STRING), 6 gaheld (HASH tier -> qty),
--       7 new hold (HASH; unused for 'free'), 8 old hold (HASH; 'pass' only, else unused), 9 old holder's userhold ('pass' only),
--       10.. GA remaining of every GA tier of the event, in the order of ARGV[10] ('free' only)
-- ARGV: 1 mode ('return' | 'pass' | 'free'), 2 new holdId, 3 new holder userId, 4 seats csv, 5 ga csv "tier:qty,...",
--       6 now ms, 7 ttl ms, 8 delta channel, 9 old holdId ('pass'), 10 GA tier ids csv
-- Modes:
--   return  sold seats / GA units of a voided ticket become a waitlist hold of the new holder; the previous owner's bought drops.
--   pass    an expired waitlist hold moves, seats and GA as they are, to the next holder (nobody ever sees them free).
--   free    nobody waits: the returned seats / GA go back to the public sale; the previous owner's bought drops.
-- Returns {'OK', expiresAtMs, version} | {'HAS_HOLD'} (the new holder already holds tickets of the event)
--       | {'TAKEN', seat...} (a seat is held by someone else) | {'NOT_FOUND'} ('pass': the old hold is gone).
-- Waitlist holds are never added to the expiry ZSET: the waitlist service expires them (the sweeper would free them).

local function split(s)
  local out = {}
  if s == nil or s == '' then return out end
  for part in string.gmatch(s, '[^,]+') do out[#out + 1] = part end
  return out
end

local mode = ARGV[1]
local holdId, userId = ARGV[2], ARGV[3]
local now, ttl = tonumber(ARGV[6]), tonumber(ARGV[7])
local seatsCsv, gaCsv = ARGV[4], ARGV[5]

if mode == 'pass' then
  local old = redis.call('HMGET', KEYS[8], 'seats', 'ga')
  if redis.call('EXISTS', KEYS[8]) == 0 then return {'NOT_FOUND'} end
  seatsCsv, gaCsv = old[1] or '', old[2] or ''
end

local seats = split(seatsCsv)
local units = #seats
for _, part in ipairs(split(gaCsv)) do
  local _, qty = string.match(part, '([^:]+):(%d+)')
  units = units + tonumber(qty)
end

if mode ~= 'free' and (redis.call('GET', KEYS[3]) or '') ~= '' then return {'HAS_HOLD'} end

local taken = {}
for _, seat in ipairs(seats) do
  local owner = redis.call('HGET', KEYS[2], seat)
  if owner and not (mode == 'pass' and owner == ARGV[9]) then taken[#taken + 1] = seat end
end
if #taken > 0 then
  table.insert(taken, 1, 'TAKEN')
  return taken
end

-- Checks passed: write.
local held, freed, tiers = {}, {}, {}
if mode == 'free' then
  local gaKey = {}
  for i, tier in ipairs(split(ARGV[10])) do gaKey[tier] = KEYS[9 + i] end
  for _, seat in ipairs(seats) do
    redis.call('SREM', KEYS[1], seat)
    freed[#freed + 1] = seat
  end
  for _, part in ipairs(split(gaCsv)) do
    local tier, qty = string.match(part, '([^:]+):(%d+)')
    if gaKey[tier] then tiers[#tiers + 1] = '"' .. tier .. '":' .. redis.call('INCRBY', gaKey[tier], tonumber(qty)) end
  end
else
  for _, seat in ipairs(seats) do
    redis.call('SREM', KEYS[1], seat)
    redis.call('HSET', KEYS[2], seat, holdId)
    if mode == 'return' then held[#held + 1] = seat end
  end
  if mode == 'return' then
    -- Sold units become held: GA remaining does not move, gaheld grows.
    for _, part in ipairs(split(gaCsv)) do
      local tier, qty = string.match(part, '([^:]+):(%d+)')
      redis.call('HINCRBY', KEYS[6], tier, tonumber(qty))
    end
  else
    redis.call('DEL', KEYS[8])
    if redis.call('GET', KEYS[9]) == ARGV[9] then redis.call('DEL', KEYS[9]) end
  end
end

if mode ~= 'pass' then
  local bought = tonumber(redis.call('GET', KEYS[4]) or '0')
  redis.call('SET', KEYS[4], math.max(0, bought - units))
end

local expiresAt = now + ttl
if mode ~= 'free' then
  redis.call('HSET', KEYS[7], 'userId', userId, 'seats', seatsCsv, 'ga', gaCsv, 'expiresAt', expiresAt, 'waitlist', '1')
  redis.call('SET', KEYS[3], holdId, 'PX', ttl + 300000)
end

local version = redis.call('INCR', KEYS[5])
redis.call('PUBLISH', ARGV[8], '{"v":' .. version .. ',"held":[' .. table.concat(held, ',') .. '],"freed":['
  .. table.concat(freed, ',') .. '],"sold":[],"ga":{' .. table.concat(tiers, ',') .. '}}')
return {'OK', tostring(expiresAt), version}
