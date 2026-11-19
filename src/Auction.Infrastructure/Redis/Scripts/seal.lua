-- seal.lua: Live -> Sealed at the current end time (spec 05 design 5, R4.1, R4.2).
-- The ends index can be stale after anti-sniping: then the auction is re-scored, not sealed.
-- After this, bid.lua answers NOT_LIVE (R4.3).
--
-- KEYS: see state_emit.lua
-- ARGV: 1 auctionId
-- Returns 'sealed', 'not_due' (end moved later; re-scored), 'skip' or 'missing'.

--@include state_emit.lua

local s = redis.call('HMGET', KEYS[1], 'status', 'endMs')
if not s[1] then
  redis.call('ZREM', KEYS[5], ARGV[1])
  return 'missing'
end
if s[1] ~= 'Live' then
  -- Frozen, Cancelled, already Sealed...: resume puts a Frozen auction back in the index.
  redis.call('ZREM', KEYS[5], ARGV[1])
  return 'skip'
end

local now = nowMs()
local endMs = tonumber(s[2]) or 0
if now < endMs then
  redis.call('ZADD', KEYS[5], n(endMs), ARGV[1])
  return 'not_due'
end

redis.call('HSET', KEYS[1], 'status', 'Sealed', 'sealedAtMs', n(now))
redis.call('ZREM', KEYS[5], ARGV[1])
redis.call('ZREM', KEYS[6], ARGV[1])
redis.call('ZADD', KEYS[7], n(now), ARGV[1])
emitState('Sealed', 'Sealed', now, { { 'endMs', n(endMs), true } })
return 'sealed'
