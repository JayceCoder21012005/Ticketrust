-- Makes an event's Redis inventory agree with the database and marks it warm (spec 07 warmer + reconciler).
-- KEYS: 1 sold (SET), 2 meta (HASH), 3 version (STRING), 4 gaheld (HASH), 5 commits (STRING),
--       6..5+T GA remaining per tier (STRING, order of ARGV[3]), then one bought key per user (order of ARGV[6])
-- ARGV: 1 sold seats csv (database), 2 seat count, 3 GA tier ids csv, 4 GA capacities csv, 5 GA sold csv,
--       6 bought counts csv, 7 delta channel, 8 commits counter read before the database ('' = no fence)
-- Returns {soldAdded, soldRemoved, tier1, before1, after1, ...} for every GA tier that changed, or {'RETRY'} when a
-- checkout committed after the database was read: its seats and GA are missing from ARGV, so nothing is written.
-- GA remaining = capacity - sold (database) - held (live holds).
-- Bought: with a fence (ARGV[8]) it is set to EXACTLY the database count. That is safe because a Redis commit always follows
-- its database commit: a commit already in Redis before the fence was read is in the database read, and one that landed
-- after the fence read trips the fence ('RETRY'). It also repairs a count inflated when this script ran between an order's
-- database commit and its (outbox) inventory_commit, which then counted the order a second time. Without a fence the count
-- only grows, so a commit that reached Redis but not yet this read of the database is never undone.

local function split(s)
  local out = {}
  if s == nil or s == '' then return out end
  for part in string.gmatch(s, '[^,]+') do out[#out + 1] = part end
  return out
end

if ARGV[8] ~= '' and tonumber(redis.call('GET', KEYS[5]) or '0') ~= tonumber(ARGV[8]) then return {'RETRY'} end

local dbSold = {}
for _, seat in ipairs(split(ARGV[1])) do dbSold[seat] = true end

local added, removed = {}, {}
for seat in pairs(dbSold) do
  if redis.call('SADD', KEYS[1], seat) == 1 then added[#added + 1] = seat end
end
for _, seat in ipairs(redis.call('SMEMBERS', KEYS[1])) do
  if not dbSold[seat] then
    redis.call('SREM', KEYS[1], seat)
    removed[#removed + 1] = seat
  end
end

local tierIds, caps, sold = split(ARGV[3]), split(ARGV[4]), split(ARGV[5])
local result = {#added, #removed}
local tiers = {}
redis.call('HSET', KEYS[2], 'warm', 1, 'seatCount', ARGV[2])
for i, tier in ipairs(tierIds) do
  local held = tonumber(redis.call('HGET', KEYS[4], tier) or '0')
  local target = tonumber(caps[i]) - tonumber(sold[i]) - held
  if target < 0 then target = 0 end
  local before = tonumber(redis.call('GET', KEYS[5 + i]) or '0')
  local exists = redis.call('EXISTS', KEYS[5 + i]) == 1
  if before ~= target or not exists then
    redis.call('SET', KEYS[5 + i], target)
    result[#result + 1] = tier
    result[#result + 1] = before
    result[#result + 1] = target
    tiers[#tiers + 1] = '"' .. tier .. '":' .. target
  end
  redis.call('HSET', KEYS[2], 'gaCap:' .. tier, caps[i])
end

local bought = split(ARGV[6])
for i, count in ipairs(bought) do
  local key = KEYS[5 + #tierIds + i]
  if ARGV[8] ~= '' or tonumber(redis.call('GET', key) or '0') < tonumber(count) then redis.call('SET', key, count) end
end

if #added > 0 or #removed > 0 or #tiers > 0 then
  local version = redis.call('INCR', KEYS[3])
  redis.call('PUBLISH', ARGV[7], '{"v":' .. version .. ',"held":[],"freed":[' .. table.concat(removed, ',')
    .. '],"sold":[' .. table.concat(added, ',') .. '],"ga":{' .. table.concat(tiers, ',') .. '}}')
end
return result
