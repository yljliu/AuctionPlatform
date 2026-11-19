-- watchers.lua: change the watcher count of an auction loaded in Redis (spec 07 R1.4).
-- Only when the state exists, so an auction not loaded yet gets its count from SQL at load time.
-- Never goes below 0.
--
-- KEYS: 1 state
-- ARGV: 1 delta (1 or -1)
-- Returns the new count, or -1 when the auction is not in Redis.

if redis.call('EXISTS', KEYS[1]) == 0 then return -1 end
local n = redis.call('HINCRBY', KEYS[1], 'watchers', tonumber(ARGV[1]))
if n < 0 then
  redis.call('HSET', KEYS[1], 'watchers', 0)
  n = 0
end
return n
