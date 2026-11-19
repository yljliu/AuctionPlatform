-- rebuild.lua: SQL says Live but Redis lost the state; build it again from SQL, Frozen (R7.2).
-- An admin checks it and resumes it. Only runs when the state is missing.
--
-- KEYS: see state_emit.lua (uses 1 state, 2 events, 3 stream, 8 max)
-- ARGV: 1 auctionId  2 sellerId  3 startMs  4 endMs  5 schedEndMs  6 startC  7 reserveC ('' = none)
--       8 snipeMs  9 extMs  10 maxExtMs  11 extUsedMs  12 inc table  13 currency
--       14 priceC  15 leaderId ('' = none)  16 leaderMaxC  17 bidCount  18 seq  19 eseq
--       20.. pairs of userId, maxC for the private max hash
-- Returns 'rebuilt' or 'exists'.

--@include state_emit.lua

if redis.call('EXISTS', KEYS[1]) == 1 then return 'exists' end

local now = nowMs()
redis.call('HSET', KEYS[1],
  'status', 'Frozen', 'frozenAtMs', n(now), 'seller', ARGV[2], 'startMs', ARGV[3], 'endMs', ARGV[4],
  'schedEndMs', ARGV[5], 'startC', ARGV[6], 'reserveC', ARGV[7], 'snipeMs', ARGV[8], 'extMs', ARGV[9],
  'maxExtMs', ARGV[10], 'extUsedMs', ARGV[11], 'inc', ARGV[12], 'cur', ARGV[13], 'priceC', ARGV[14],
  'leader', ARGV[15], 'leaderMaxC', ARGV[16], 'bidCount', ARGV[17], 'seq', ARGV[18], 'eseq', ARGV[19],
  'persistedEseq', ARGV[19], 'watchers', 0)

redis.call('DEL', KEYS[8])
for i = 20, #ARGV - 1, 2 do
  redis.call('HSET', KEYS[8], ARGV[i], ARGV[i + 1])
end

emitState('Frozen', 'Frozen', now, { { 'reason', 'rebuilt', false } })
return 'rebuilt'
