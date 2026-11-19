-- control.lua: seller and admin actions on a running auction (spec 05 design 5, R6).
-- Each op checks the status first, so a bid and a cancel can never both win (R6.1).
--
-- KEYS: see state_emit.lua
-- ARGV: 1 auctionId  2 op (cancel|close|freeze|resume)  3 force ('1' = admin, cancel even with bids)
-- Returns 'ok', 'invalid_state' or 'missing'.

--@include state_emit.lua

local s = redis.call('HMGET', KEYS[1], 'status', 'bidCount', 'endMs', 'frozenAtMs', 'soonSent')
if not s[1] then return 'missing' end

local op = ARGV[2]
local status = s[1]
local now = nowMs()

if op == 'cancel' then
  local force = ARGV[3] == '1'
  local open = status == 'Live' or status == 'Scheduled' or (force and status == 'Frozen')
  if not open then return 'invalid_state' end
  -- A seller may cancel only while no bid has landed; a bid that got in first wins.
  if not force and (tonumber(s[2]) or 0) > 0 then return 'invalid_state' end
  redis.call('HSET', KEYS[1], 'status', 'Cancelled')
  unindex()
  emitState('Cancelled', 'Cancelled', now, {})
  expireAll()
  return 'ok'
end

if op == 'close' then
  if status ~= 'Live' then return 'invalid_state' end
  -- End now; the normal seal path runs on the next tick (R6.3). No event of its own.
  redis.call('HSET', KEYS[1], 'endMs', n(now))
  redis.call('ZADD', KEYS[5], n(now), ARGV[1])
  redis.call('ZREM', KEYS[6], ARGV[1])
  return 'ok'
end

if op == 'freeze' then
  if status ~= 'Live' then return 'invalid_state' end
  redis.call('HSET', KEYS[1], 'status', 'Frozen', 'frozenAtMs', n(now))
  redis.call('ZREM', KEYS[5], ARGV[1])
  redis.call('ZREM', KEYS[6], ARGV[1])
  emitState('Frozen', 'Frozen', now, {})
  return 'ok'
end

if op == 'resume' then
  if status ~= 'Frozen' then return 'invalid_state' end
  -- The time spent frozen is added to the end, and at least 60 s are left (R6.4).
  local frozenAt = tonumber(s[4]) or now
  local endMs = (tonumber(s[3]) or now) + (now - frozenAt)
  if endMs < now + 60000 then endMs = now + 60000 end
  redis.call('HSET', KEYS[1], 'status', 'Live', 'endMs', n(endMs))
  redis.call('HDEL', KEYS[1], 'frozenAtMs')
  redis.call('ZADD', KEYS[5], n(endMs), ARGV[1])
  if not s[5] then redis.call('ZADD', KEYS[6], n(endMs - 60000), ARGV[1]) end
  emitState('Live', 'Resumed', now, { { 'endMs', n(endMs), true } })
  return 'ok'
end

return redis.error_reply('UNKNOWN_OP ' .. tostring(op))
