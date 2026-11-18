-- bid.lua: place one bid on one auction, all in one step (spec 03 design 4).
-- Redis runs a script alone, so no two bids can ever see the same old price (R7.1).
-- The proxy rules below must match ProxyResolver.cs exactly (tests/vectors/proxy.json).
--
-- KEYS: 1 state  2 max  3 bids  4 events  5 idem  6 stream
-- ARGV: 1 userId  2 maxC  3 fingerprint  4 correlationId  5 idemKey  6 auctionId  7 currency
--
-- Returns { outcome, replayed }
--   outcome "ok|<Result>|priceC|minNextC|seq|endMs|extended|bidCount|isLeader"
--           "err|<CODE>|minNextC"
--   replayed 1 when the outcome came from an earlier call with the same idempotency key.
-- Only whole cents are used. Lua numbers are doubles, which are exact for whole numbers below 2^53.

local function n(x) return string.format('%.0f', x) end

-- 1. Same idempotency key seen before: give back the first outcome, or refuse a different bid.
local stored = redis.call('GET', KEYS[5])
if stored then
  local cut = string.find(stored, '#', 1, true)
  if (not cut) or string.sub(stored, 1, cut - 1) ~= ARGV[3] then
    return { 'err|IDEMPOTENCY_MISMATCH|0', 0 }
  end
  return { string.sub(stored, cut + 1), 1 }
end

local user = ARGV[1]
local maxC = tonumber(ARGV[2])

local s = redis.call('HMGET', KEYS[1],
  'status', 'seller', 'startMs', 'endMs', 'startC', 'priceC', 'leader', 'leaderMaxC', 'bidCount',
  'seq', 'eseq', 'snipeMs', 'extMs', 'maxExtMs', 'extUsedMs', 'inc', 'cur')

-- Not loaded into Redis: the Api looks in SQL. Not stored, so a retry after loading works.
if not s[1] then return { 'err|MISSING|0', 0 } end
-- Wrong currency is an input mistake. Not stored, so the caller can fix it.
if s[17] and ARGV[7] ~= s[17] then return { 'err|CURRENCY|0', 0 } end

local tiers = {}
for from, inc in string.gmatch(s[16] or '', '(%d+):(%d+)') do
  tiers[#tiers + 1] = { tonumber(from), tonumber(inc) }
end
if #tiers == 0 then return { 'err|BAD_STATE|0', 0 } end

local function incFor(price)
  local value = tiers[1][2]
  for i = 1, #tiers do
    if tiers[i][1] <= price then value = tiers[i][2] else break end
  end
  return value
end

-- Every outcome from here on is stored for 24 hours under the idempotency key (R6.1).
local function finish(outcome)
  redis.call('SET', KEYS[5], ARGV[3] .. '#' .. outcome, 'EX', 86400)
  return { outcome, 0 }
end

local function reject(code, minC)
  return finish('err|' .. code .. '|' .. n(minC or 0))
end

-- 2. Server time only (R2.3).
local t = redis.call('TIME')
local now = tonumber(t[1]) * 1000 + math.floor(tonumber(t[2]) / 1000)

-- 3. Is the auction open, and is this not the seller?
local status = s[1]
local startMs = tonumber(s[3]) or 0
local endMs = tonumber(s[4]) or 0
if status == 'Frozen' then return reject('AUCTION_FROZEN') end
if status ~= 'Live' then return reject('NOT_LIVE') end
if now < startMs then return reject('NOT_LIVE') end
if now >= endMs then return reject('ENDED') end
if user == s[2] then return reject('OWN_LOT') end

local startC = tonumber(s[5]) or 0
local price = tonumber(s[6]) or 0
local leader = s[7]
if leader == '' then leader = false end
local leaderMax = tonumber(s[8]) or 0
local bidCount = tonumber(s[9]) or 0
local seq = tonumber(s[10]) or 0
local eseq = tonumber(s[11]) or 0
local snipeMs = tonumber(s[12]) or 0
local extMs = tonumber(s[13]) or 0
local maxExtMs = tonumber(s[14]) or 0
local extUsedMs = tonumber(s[15]) or 0

-- 4. Proxy rules (same order as ProxyResolver.Resolve).
local result
local outbid = ''
local rows = {}

if bidCount == 0 then
  if maxC < startC then return reject('BELOW_MINIMUM', startC) end
  result = 'Leading'
  price = startC
  leader = user
  leaderMax = maxC
  bidCount = 1
  rows[1] = { user, startC, 'Manual' }
elseif user == leader then
  if maxC <= leaderMax then return reject('BELOW_MINIMUM', leaderMax + 1) end
  result = 'MaxRaised'
  leaderMax = maxC
  rows[1] = { user, price, 'MaxRaise' }
else
  local minNext = price + incFor(price)
  if maxC < minNext then return reject('BELOW_MINIMUM', minNext) end
  if maxC > leaderMax then
    local target = math.max(minNext, leaderMax + incFor(leaderMax))
    local newPrice = math.min(maxC, target)
    if leaderMax > price then rows[#rows + 1] = { leader, leaderMax, 'ProxyAuto' } end
    rows[#rows + 1] = { user, newPrice, 'Manual' }
    outbid = leader
    leader = user
    leaderMax = maxC
    price = newPrice
    bidCount = bidCount + 1
    result = 'Leading'
  else
    local answer = math.min(leaderMax, maxC + incFor(maxC))
    rows[1] = { user, maxC, 'AutoOutbid' }
    rows[2] = { leader, answer, 'ProxyAuto' }
    price = answer
    bidCount = bidCount + 1
    result = 'OutbidByProxy'
  end
end

-- 5. Remember this bidder's highest max (private).
local myOld = tonumber(redis.call('HGET', KEYS[2], user) or '0') or 0
if maxC > myOld then redis.call('HSET', KEYS[2], user, n(maxC)) end

-- 6. Anti-sniping: a price change near the end adds time, up to the cap (R5). MaxRaised never does.
local extended = 0
if result ~= 'MaxRaised' and endMs - now <= snipeMs and extUsedMs < maxExtMs then
  local add = math.min(extMs, maxExtMs - extUsedMs)
  if add > 0 then
    endMs = endMs + add
    extUsedMs = extUsedMs + add
    extended = 1
  end
end

-- 7. Counters. seq is public and only counts price changes; eseq counts every stream entry.
eseq = eseq + 1
if result ~= 'MaxRaised' then seq = seq + 1 end
redis.call('HSET', KEYS[1],
  'priceC', n(price), 'leader', leader, 'leaderMaxC', n(leaderMax), 'bidCount', n(bidCount),
  'seq', n(seq), 'eseq', n(eseq), 'endMs', n(endMs), 'extUsedMs', n(extUsedMs))

-- 8. Public rows (no max values; MaxRaise rows stay private) and the public event.
local all, public = {}, {}
for i, r in ipairs(rows) do
  local json = '{"seq":' .. n(seq) .. ',"user":"' .. r[1] .. '","amountC":' .. n(r[2]) ..
               ',"atMs":' .. n(now) .. ',"kind":"' .. r[3] .. '"}'
  all[#all + 1] = json
  if r[3] ~= 'MaxRaise' then
    public[#public + 1] = json
    redis.call('ZADD', KEYS[3], n(eseq * 10 + i), json)
  end
end
redis.call('ZREMRANGEBYRANK', KEYS[3], 0, -51)

if result ~= 'MaxRaised' then
  local event = '{"seq":' .. n(seq) .. ',"type":"Bid","priceC":' .. n(price) .. ',"leader":"' .. leader ..
                '","bidCount":' .. n(bidCount) .. ',"endMs":' .. n(endMs) ..
                ',"extended":' .. (extended == 1 and 'true' or 'false') ..
                ',"rows":[' .. table.concat(public, ',') .. ']}'
  redis.call('ZADD', KEYS[4], n(seq), event)
  redis.call('ZREMRANGEBYRANK', KEYS[4], 0, -201)
end

-- 9. Stream entry for the persister (spec 03 task 8). Never trimmed here.
redis.call('XADD', KEYS[6], '*',
  'type', 'Bid', 'auction', ARGV[6], 'eseq', n(eseq), 'seq', (result ~= 'MaxRaised') and n(seq) or '-',
  'user', user, 'maxC', n(maxC), 'result', result, 'priceC', n(price), 'leader', leader,
  'leaderMaxC', n(leaderMax), 'bidCount', n(bidCount), 'endMs', n(endMs), 'extended', n(extended),
  'rows', '[' .. table.concat(all, ',') .. ']', 'outbid', outbid, 'idem', ARGV[5], 'at', n(now), 'corr', ARGV[4])

-- 10. Store and return the outcome.
local minNextC = price + incFor(price)
return finish('ok|' .. result .. '|' .. n(price) .. '|' .. n(minNextC) .. '|' .. n(seq) .. '|' .. n(endMs) ..
              '|' .. n(extended) .. '|' .. n(bidCount) .. '|' .. ((leader == user) and '1' or '0'))
