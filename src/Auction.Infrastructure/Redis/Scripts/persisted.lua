-- persisted.lua: record that SQL now holds every stream entry up to this eseq (spec 03 design 6).
-- Only moves forward, so an old batch finishing late can never move it back.
-- Does nothing when the auction is no longer in Redis.
--
-- KEYS: 1 state
-- ARGV: 1 eseq
-- Returns the stored value after the call (0 when there is no state).

if redis.call('EXISTS', KEYS[1]) == 0 then return 0 end

local wanted = tonumber(ARGV[1])
local current = tonumber(redis.call('HGET', KEYS[1], 'persistedEseq') or '0') or 0
if wanted > current then
  redis.call('HSET', KEYS[1], 'persistedEseq', string.format('%.0f', wanted))
  return wanted
end
return current
