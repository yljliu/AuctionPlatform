-- freeze.lua: emergency freeze used by the persister when an entry cannot be written (spec 03 R8.5).
-- Unlike control.lua freeze it works from any status but Frozen, because SQL can no longer be trusted.
-- bid.lua then answers AUCTION_FROZEN.
--
-- KEYS: 1 state  2 events  3 stream   (the first three keys of state_emit.lua)
-- ARGV: 1 auctionId  2 reason
-- Returns 1 when it froze the auction, 0 when there was nothing to do.

--@include state_emit.lua

if redis.call('EXISTS', KEYS[1]) == 0 then return 0 end
if redis.call('HGET', KEYS[1], 'status') == 'Frozen' then return 0 end

local now = nowMs()
redis.call('HSET', KEYS[1], 'status', 'Frozen', 'frozenAtMs', n(now))
emitState('Frozen', 'Frozen', now, { { 'reason', ARGV[2], false } })
return 1
