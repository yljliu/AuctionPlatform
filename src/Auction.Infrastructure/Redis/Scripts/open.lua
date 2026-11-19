-- open.lua: Scheduled -> Live at the start time (spec 05 design 5, R2.2, R2.3).
-- Running it twice gives one event: the second run sees Live and does nothing.
--
-- KEYS: see state_emit.lua
-- ARGV: 1 auctionId
-- Returns 'opened', 'not_due' (start time not reached), 'skip' (not Scheduled) or 'missing'.

--@include state_emit.lua

local s = redis.call('HMGET', KEYS[1], 'status', 'startMs', 'endMs')
if not s[1] then
  redis.call('ZREM', KEYS[4], ARGV[1])
  return 'missing'
end
if s[1] ~= 'Scheduled' then
  redis.call('ZREM', KEYS[4], ARGV[1])
  return 'skip'
end

local now = nowMs()
local startMs = tonumber(s[2]) or 0
local endMs = tonumber(s[3]) or 0
if now < startMs then
  redis.call('ZADD', KEYS[4], n(startMs), ARGV[1])
  return 'not_due'
end

redis.call('HSET', KEYS[1], 'status', 'Live')
redis.call('ZREM', KEYS[4], ARGV[1])
redis.call('ZADD', KEYS[5], n(endMs), ARGV[1])
redis.call('ZADD', KEYS[6], n(endMs - 60000), ARGV[1])
emitState('Live', 'Opened', now, { { 'endMs', n(endMs), true } })
return 'opened'
