-- load.lua: put a Scheduled auction into Redis before it starts (spec 05 design 5, R2.1).
-- Creates the state only when it is missing, so a second load never resets a running auction.
--
-- KEYS: see state_emit.lua (uses 1 state, 4 starts)
-- ARGV: 1 auctionId  2 sellerId  3 startMs  4 endMs  5 startC  6 reserveC ('' = none)
--       7 snipeMs  8 extMs  9 maxExtMs  10 inc table  11 currency
-- Returns 'loaded', or 'exists' when the state was already there.

--@include state_emit.lua

if redis.call('EXISTS', KEYS[1]) == 1 then
  -- Already loaded. Still Scheduled: make sure it is in the starts index (recovery calls this too).
  if redis.call('HGET', KEYS[1], 'status') == 'Scheduled' then
    redis.call('ZADD', KEYS[4], redis.call('HGET', KEYS[1], 'startMs'), ARGV[1])
  end
  return 'exists'
end

redis.call('HSET', KEYS[1],
  'status', 'Scheduled', 'seller', ARGV[2], 'startMs', ARGV[3], 'endMs', ARGV[4], 'schedEndMs', ARGV[4],
  'startC', ARGV[5], 'reserveC', ARGV[6], 'priceC', 0, 'leader', '', 'leaderMaxC', 0, 'bidCount', 0,
  'seq', 0, 'eseq', 0, 'persistedEseq', 0, 'snipeMs', ARGV[7], 'extMs', ARGV[8], 'maxExtMs', ARGV[9],
  'extUsedMs', 0, 'inc', ARGV[10], 'cur', ARGV[11], 'watchers', 0)
redis.call('ZADD', KEYS[4], ARGV[3], ARGV[1])
return 'loaded'
