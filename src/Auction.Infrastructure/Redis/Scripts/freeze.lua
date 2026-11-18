-- freeze.lua (stub for spec 03; spec 05 owns the full version).
-- Pauses one auction: status Frozen, so bid.lua answers AUCTION_FROZEN.
-- Writes a public State event and a stream entry like every other change.
--
-- KEYS: 1 state  2 events  3 stream
-- ARGV: 1 auctionId  2 reason
-- Returns 1 when it froze the auction, 0 when there was nothing to do.

if redis.call('EXISTS', KEYS[1]) == 0 then return 0 end
if redis.call('HGET', KEYS[1], 'status') == 'Frozen' then return 0 end

local function n(x) return string.format('%.0f', x) end
local t = redis.call('TIME')
local now = tonumber(t[1]) * 1000 + math.floor(tonumber(t[2]) / 1000)

local seq = redis.call('HINCRBY', KEYS[1], 'seq', 1)
local eseq = redis.call('HINCRBY', KEYS[1], 'eseq', 1)
redis.call('HSET', KEYS[1], 'status', 'Frozen', 'frozenAtMs', n(now))

local event = '{"seq":' .. n(seq) .. ',"type":"State","status":"Frozen","atMs":' .. n(now) .. '}'
redis.call('ZADD', KEYS[2], n(seq), event)
redis.call('ZREMRANGEBYRANK', KEYS[2], 0, -201)

redis.call('XADD', KEYS[3], '*',
  'type', 'State', 'auction', ARGV[1], 'status', 'Frozen', 'seq', n(seq), 'eseq', n(eseq),
  'at', n(now), 'reason', ARGV[2])
return 1
