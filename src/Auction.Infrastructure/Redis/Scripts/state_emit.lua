-- state_emit.lua: shared part of every lifecycle script (spec 05 design 5).
-- Not run on its own: LuaScripts.Load pastes it into each script at that script's include line.
--
-- Every lifecycle script uses the same KEYS:
--   1 state  2 events  3 stream  4 starts  5 ends  6 soon  7 sealed  8 max  9 bids
-- and ARGV[1] is the auction id.

local function n(x) return string.format('%.0f', x) end

local function nowMs()
  local t = redis.call('TIME')
  return tonumber(t[1]) * 1000 + math.floor(tonumber(t[2]) / 1000)
end

-- One public State event: seq + 1, eseq + 1, a row in the events ZSET (catch-up) and a stream entry
-- (persister and live readers). status is the auction status after the change (Live, Sealed, ...);
-- change names what happened (Opened, ClosingSoon, Sealed, Settled, Cancelled, Frozen, Resumed).
-- extra is a list of { name, value, isNumber } pairs added to both. Nothing private goes in here.
local function emitState(status, change, now, extra)
  local seq = redis.call('HINCRBY', KEYS[1], 'seq', 1)
  local eseq = redis.call('HINCRBY', KEYS[1], 'eseq', 1)

  local json = '{"seq":' .. n(seq) .. ',"type":"State","status":"' .. status .. '","change":"' .. change ..
               '","atMs":' .. n(now)
  local fields = { 'type', 'State', 'auction', ARGV[1], 'status', status, 'change', change,
                   'seq', n(seq), 'eseq', n(eseq), 'at', n(now) }
  for _, e in ipairs(extra or {}) do
    if e[3] then json = json .. ',"' .. e[1] .. '":' .. e[2]
    else json = json .. ',"' .. e[1] .. '":"' .. e[2] .. '"' end
    fields[#fields + 1] = e[1]
    fields[#fields + 1] = e[2]
  end
  json = json .. '}'

  redis.call('ZADD', KEYS[2], n(seq), json)
  redis.call('ZREMRANGEBYRANK', KEYS[2], 0, -201)
  redis.call('XADD', KEYS[3], '*', unpack(fields))
  return seq
end

-- Removes the auction from every partition index set.
local function unindex()
  redis.call('ZREM', KEYS[4], ARGV[1])
  redis.call('ZREM', KEYS[5], ARGV[1])
  redis.call('ZREM', KEYS[6], ARGV[1])
  redis.call('ZREM', KEYS[7], ARGV[1])
end

-- A finished auction keeps its Redis keys 10 minutes, so late viewers still get the final state.
local function expireAll()
  redis.call('EXPIRE', KEYS[1], 600)
  redis.call('EXPIRE', KEYS[2], 600)
  redis.call('EXPIRE', KEYS[8], 600)
  redis.call('EXPIRE', KEYS[9], 600)
end
