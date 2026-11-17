-- Token bucket rate limiter.
-- KEYS[1] = rl:{scope}:{id}   hash fields: tokens, ts (ms)
-- ARGV[1] = capacity, ARGV[2] = refill tokens per second, ARGV[3] = cost
-- Returns { allowed (1 or 0), retry_after_ms }

local key = KEYS[1]
local capacity = tonumber(ARGV[1])
local rate = tonumber(ARGV[2])
local cost = tonumber(ARGV[3])

local t = redis.call('TIME')
local now = tonumber(t[1]) * 1000 + math.floor(tonumber(t[2]) / 1000)

local saved = redis.call('HMGET', key, 'tokens', 'ts')
local tokens = tonumber(saved[1])
local ts = tonumber(saved[2])
if tokens == nil or ts == nil then
  tokens = capacity
  ts = now
end

local elapsed = now - ts
if elapsed < 0 then elapsed = 0 end
tokens = math.min(capacity, tokens + elapsed * rate / 1000)

local allowed = 0
local retry = 0
if tokens >= cost then
  tokens = tokens - cost
  allowed = 1
else
  retry = math.ceil((cost - tokens) * 1000 / rate)
end

redis.call('HSET', key, 'tokens', tostring(tokens), 'ts', tostring(now))
redis.call('PEXPIRE', key, math.ceil(capacity * 1000 / rate) + 1000)

return { allowed, retry }
