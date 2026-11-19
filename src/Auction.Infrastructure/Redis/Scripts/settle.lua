-- settle.lua: Sealed -> Settled, after SQL committed the settlement (spec 05 design 5, R5.6).
-- Safe to run again: a Settled auction gives 'skip' and no second event.
--
-- KEYS: see state_emit.lua
-- ARGV: 1 auctionId  2 outcome (Sold|Unsold)  3 finalC (0 when Unsold)  4 winnerId ('' when Unsold)
-- Returns 'settled', 'skip' or 'missing'.

--@include state_emit.lua

local status = redis.call('HGET', KEYS[1], 'status')
if not status then
  redis.call('ZREM', KEYS[7], ARGV[1])
  return 'missing'
end
if status ~= 'Sealed' then
  redis.call('ZREM', KEYS[7], ARGV[1])
  return 'skip'
end

local now = nowMs()
redis.call('HSET', KEYS[1], 'status', 'Settled', 'outcome', ARGV[2], 'finalC', ARGV[3], 'winner', ARGV[4])
unindex()
emitState('Settled', 'Settled', now, {
  { 'outcome', ARGV[2], false }, { 'finalC', n(tonumber(ARGV[3]) or 0), true }, { 'winner', ARGV[4], false } })
expireAll()
return 'settled'
