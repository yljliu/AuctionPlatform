-- soon.lua: one ClosingSoon event when 60 s or less are left (spec 05 design 5, R3.1).
-- soonSent makes it once per auction, even after anti-sniping moves the end later.
--
-- KEYS: see state_emit.lua
-- ARGV: 1 auctionId
-- Returns 'sent', 'not_due' (end moved later; re-scored), 'skip' or 'missing'.

--@include state_emit.lua

local s = redis.call('HMGET', KEYS[1], 'status', 'endMs', 'soonSent')
if not s[1] then
  redis.call('ZREM', KEYS[6], ARGV[1])
  return 'missing'
end
if s[1] ~= 'Live' or s[3] then
  redis.call('ZREM', KEYS[6], ARGV[1])
  return 'skip'
end

local now = nowMs()
local endMs = tonumber(s[2]) or 0
if now < endMs - 60000 then
  redis.call('ZADD', KEYS[6], n(endMs - 60000), ARGV[1])
  return 'not_due'
end

redis.call('HSET', KEYS[1], 'soonSent', 1)
redis.call('ZREM', KEYS[6], ARGV[1])
emitState('Live', 'ClosingSoon', now, { { 'endMs', n(endMs), true } })
return 'sent'
