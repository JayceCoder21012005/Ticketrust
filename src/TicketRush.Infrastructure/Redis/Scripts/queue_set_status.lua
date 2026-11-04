-- Changes a waiting room's status only from an allowed one (spec 06 inventory listener, cancellation).
-- KEYS: 1 state (HASH)
-- ARGV: 1 new status, 2 allowed current statuses csv ('*' = any)
-- Returns the status before the call ('' when the room does not exist; a missing room is never created).
local current = redis.call('HGET', KEYS[1], 'status')
if not current then return '' end
if ARGV[2] ~= '*' then
  local allowed = false
  for status in string.gmatch(ARGV[2], '[^,]+') do
    if status == current then allowed = true end
  end
  if not allowed then return current end
end
if current ~= ARGV[1] then redis.call('HSET', KEYS[1], 'status', ARGV[1]) end
return current
