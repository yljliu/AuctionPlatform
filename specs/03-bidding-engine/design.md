# 03 Bidding Engine: Design

## 1. Redis key layout (refines v2 §4.4)

To keep **every key the bid script touches in one Redis Cluster slot**, including the stream, all keys use a **partition hash tag** `{pN}` instead of `{a:ID}`:

`N = (uint)Murmur3(auctionId) % Partitions` (MVP `Partitions = 1`, so always `{p0}`). The partition for an auction never changes.

| Key | Type | Content |
|---|---|---|
| `{pN}:a:{id}:state` | Hash | see §2 |
| `{pN}:a:{id}:max` | Hash | `userId → maxCents` (private) |
| `{pN}:a:{id}:bids` | ZSET | score = eseq, member = public row JSON; keep last 50 |
| `{pN}:a:{id}:events` | ZSET | score = seq, member = public event JSON; keep last 200 (for catch-up) |
| `{pN}:a:{id}:idem:{key}` | String | `fingerprint|outcomeJSON`, TTL 86400 |
| `{pN}:stream` | Stream | all entries for the partition, `MAXLEN ~ 200000` trimmed only below min acked (see §6) |
| `{pN}:dlq` | Stream | failed entries |

This is a change from v2 §4.4 (`{a:ID}` tags). Update the design doc when this spec is done.

**Public bid row JSON** (member of `{pN}:a:{id}:bids`). Spec 02 already reads this shape (`PublicBidJson` in `Auction.Infrastructure/Redis/LiveStateReader.cs`), so the bid script must write exactly: `{"seq":<long>,"user":"<userId>","amountC":<long>,"atMs":<long>,"kind":"Manual|ProxyAuto|AutoOutbid"}`. Never a max. `MaxRaise` rows are private and not added to this ZSET.

## 2. State hash fields

| Field | Meaning |
|---|---|
| `status` | `Scheduled`, `Live`, `Sealed`, `Settling`, `Settled`, `Cancelled`, `Frozen` |
| `seller` | seller userId |
| `startMs`, `endMs`, `schedEndMs` | times (ms since epoch) |
| `startC` | starting price, cents |
| `reserveC` | reserve cents or `""` |
| `priceC` | current price cents (`0` if no bids) |
| `leader` | userId or `""` |
| `leaderMaxC` | cents |
| `bidCount` | number of price-changing bids |
| `seq` | last public seq |
| `eseq` | last internal entry seq (every stream entry) |
| `persistedEseq` | last eseq committed to SQL (written by persister) |
| `snipeMs`, `extMs`, `maxExtMs`, `extUsedMs` | anti-sniping settings and usage |
| `inc` | increment tiers, cents: `"0:100,5000:250,25000:500,100000:1000,500000:5000"` |
| `cur` | currency, e.g. `USD` |
| `watchers` | watcher count (spec 07) |
| `soonSent`, `sealedAtMs`, `frozenAtMs` | lifecycle flags and times (spec 05) |
| `outcome`, `finalC`, `winner` | set at settle (spec 05) |

Spec 05 creates this hash when it opens an auction.

## 3. Domain (C#) — write first

```csharp
public sealed record IncrementTable(IReadOnlyList<(long FromC, long IncC)> Tiers) {
  public long IncrementFor(long priceC);                      // last tier with FromC <= priceC
  public long MinNext(long priceC, int bidCount, long startC) => bidCount == 0 ? startC : priceC + IncrementFor(priceC);
}

public enum BidResult { Leading, OutbidByProxy, MaxRaised }
public sealed record BidState(long PriceC, Guid? Leader, long LeaderMaxC, int BidCount, long StartC);
public sealed record BidRow(Guid User, long AmountC, BidKind Kind);    // Manual, ProxyAuto, AutoOutbid, MaxRaise
public sealed record Resolution(BidResult Result, BidState Next, IReadOnlyList<BidRow> Rows, Guid? Outbid);

public static class ProxyResolver {
  // Throws DomainException(BELOW_MINIMUM) as needed.
  public static Resolution Resolve(BidState s, Guid user, long maxC, IncrementTable inc);
}
```

Rules (must match requirements R4):

| Case | Next leader | Next price | Rows (in order) | Result |
|---|---|---|---|---|
| no bids, `max ≥ start` | user | start | Manual(user, start) | Leading |
| user is leader, `max > leaderMax` | same | same | MaxRaise(user, price) | MaxRaised |
| user is leader, `max ≤ leaderMax` | — | — | reject BELOW_MINIMUM (min = leaderMax + 1 cent) | — |
| `max < minNext` | — | — | reject BELOW_MINIMUM | — |
| `max > leaderMax` | user | `min(max, leaderMax + inc(leaderMax))` | ProxyAuto(old, leaderMax) if leaderMax > price; Manual(user, newPrice) | Leading; Outbid = old |
| `max ≤ leaderMax` | old | `min(leaderMax, max + inc(max))` | AutoOutbid(user, max); ProxyAuto(old, newPrice) | OutbidByProxy |

Test vectors in `tests/vectors/proxy.json`: array of `{name, state, user, maxC, expect: {result|error, priceC, leader, leaderMaxC, rows}}`. At least 25 cases: first bid, exactly start, below start, tie, challenger 1 cent above, increment tier boundary, leader raise, leader lower, max between price and minNext, large jumps.

## 4. Bid Lua script (`bid.lua`)

KEYS: `[1] state, [2] max, [3] bids, [4] events, [5] idem, [6] stream`
ARGV: `[1] userId, [2] maxC, [3] idemFingerprint (userId:maxC), [4] correlationId`

```
1  existing = GET idem
   if existing: if prefix != fingerprint → return {err,"IDEMPOTENCY_MISMATCH"} else return stored outcome
2  t = TIME → nowMs
3  load state fields (HMGET)
   if status == "Frozen" → reject AUCTION_FROZEN; if status != "Live" → reject NOT_LIVE
   if nowMs < startMs → reject NOT_LIVE; if nowMs >= endMs → reject ENDED
   if userId == seller → reject OWN_LOT
4  resolve proxy (same table as §3) → result, newPrice, newLeader, newLeaderMax, rows, outbidUser
   on reject: outcome = {rejected, code, minNextC}; SET idem outcome EX 86400; return
5  HSET max userId maxC (if it is above the user's stored max)
6  extended = false
   if result != MaxRaised and endMs - nowMs <= snipeMs and extUsedMs < maxExtMs:
       add = min(extMs, maxExtMs - extUsedMs); endMs += add; extUsedMs += add; extended = true
7  eseq += 1
   if result != MaxRaised: seq += 1; bidCount += 1
   HSET state priceC leader leaderMaxC bidCount seq eseq endMs extUsedMs
8  for each public row: ZADD bids eseq*10+i rowJSON; ZREMRANGEBYRANK bids 0 -51
   if result != MaxRaised: event = {seq, type:"Bid", priceC, leaderHandleId, bidCount, endMs, extended, rows}
       ZADD events seq eventJSON; ZREMRANGEBYRANK events 0 -201
9  XADD stream * type Bid auction <id> eseq <eseq> seq <seq|-> user <userId> maxC <maxC>
        result <r> priceC <p> leader <l> leaderMaxC <m> endMs <e> extended <0|1>
        rows <json> outbid <userId|""> idem <key> at <nowMs> corr <correlationId>
10 outcome = {ok, result, priceC, minNextC(new), seq, endMs, extended, bidCount}
   SET idem fingerprint|outcome EX 86400
   return outcome
```

Lua numbers are doubles: cents up to 9e15 are exact. Never use floats for money in Lua other than integer cents. Increment parsing: split `inc` once per call (small string).

Loaded with `SCRIPT LOAD` at startup; called with `EVALSHA`; on `NOSCRIPT` reload and retry once.

## 5. PlaceBid in Api

```
BiddingGrpcService.PlaceBid(req)
  validate: auction_id Guid, idempotency_key Guid, amount > 0, ≤ 2 decimals, currency = auction currency (from state hash `cur`)
  rateLimiter.Check("bid", userId, 5/s)              → RATE_LIMITED
  status = userStatusCache.Get(userId)               → ACCOUNT_SUSPENDED / ACCOUNT_BANNED
  keys = RedisKeys.For(auctionId)
  r = EVALSHA bid.lua keys [userId, maxC, fingerprint, corr]   (deadline 2 s)
  if r is reject → throw DomainException(code, minNext)
  if Redis:RequireReplicaAck: n = WAIT 1 50; if n < 1 → throw SERVICE_UNAVAILABLE (retryable)
  metrics: bids_accepted_total{result}, bid_latency_ms
  return PlaceBidResponse
```

If the state hash does not exist: auction may be Scheduled, Settled or not loaded → read SQL status and return `NOT_LIVE` (or `NOT_FOUND`).

## 6. Persister (`Auction.Workers/Persister`)

One `BackgroundService` per partition (MVP: one). Consumer group `persist`, consumer name = machine name.

```
on start: XGROUP CREATE {pN}:stream persist $ MKSTREAM (ignore BUSYGROUP)
          first read pending: XREADGROUP ... STREAMS {pN}:stream 0   (own PEL)
          then XAUTOCLAIM {pN}:stream persist me 30000 0-0 (others' stuck entries)
loop:
  entries = XREADGROUP GROUP persist me COUNT 200 BLOCK 1000 STREAMS {pN}:stream >
  group by auction, keep stream order
  BEGIN TRAN (Dapper, READ COMMITTED)
    for each Bid entry:
      INSERT bidding.Bids (AuctionId, UserId, ESeq, Idx, Kind, Amount, MaxAmount, IdempotencyKey, AcceptedAt)
        for each row; skip rows whose (AuctionId, ESeq, Idx) exists   -- idempotent replay
      INSERT bidding.AuctionEvents (AuctionId, Seq, ESeq, EventType, Payload, OccurredAt) if seq present
    per auction (last entry wins):
      UPDATE catalog.Auctions SET CurrentPrice, CurrentLeaderId, BidCount, EndTime, LastSeq, LastESeq, UpdatedAt
        WHERE AuctionId = @id AND LastESeq < @eseq
      UPDATE catalog.Items SET LockedAt = SYSUTCDATETIME() WHERE ItemId = @item AND LockedAt IS NULL
  COMMIT
  HSET {pN}:a:{id}:state persistedEseq <eseq>   (per auction, only increase: Lua max-set)
  XACK {pN}:stream persist <ids>
  every 10 s: XTRIM {pN}:stream MINID <min over groups `persist` and `notify` of (oldest pending id, else last-delivered-id)>
              then keep at least the last 10 minutes (Api live readers use `$`, so they never need old entries)
on SQL error: do not ack; backoff 100 ms × 2^n, max 5 s; track attempts per entry id;
  at 10 attempts: XADD {pN}:dlq (copy) ; XACK original ; run freeze.lua for that auction ; log Critical "PersistDLQ"
```

Schema additions to v2 §4.2 (`bidding.Bids`): add `ESeq BIGINT NOT NULL`, `Idx TINYINT NOT NULL`, unique `(AuctionId, ESeq, Idx)`, `Kind` gains `MaxRaise`. `catalog.Auctions` gains `LastESeq BIGINT NOT NULL DEFAULT 0`. `bidding.AuctionEvents` gains `ESeq`. Replace `Seq, SubSeq` unique with the above.

Lag metric: `persist_lag_ms` = now − timestamp part of the oldest pending id (from `XPENDING` summary), exported every second.

## 7. Read endpoints

- `GetBidHistory`: Live/Sealed → `ZREVRANGE {pN}:a:{id}:bids` for page 1, SQL for older pages; Settled → SQL only. Mask handles (spec 02 §6).
- `GetMyBidStatus`: `HGET max userId`, `HMGET state leader priceC bidCount startC inc` → compute `min_next_bid` with `IncrementTable`.
- `ListMyBids`: SQL `Bids` grouped by auction for user + live overlay; outcome from `Auctions.Outcome` and winner.

## 8. Testing approach

- Domain: `ProxyResolver` table tests from vectors + FsCheck properties: price never decreases; price ≤ leader max; leader max = max of all maxes; tie keeps earlier leader.
- Lua: Testcontainers Redis, run every vector through `bid.lua`, compare with `ProxyResolver`.
- Concurrency: 50 parallel tasks bid on one auction 20 times each; assert `seq` has no gaps and final price equals replaying the accepted order through `ProxyResolver`.
- Persister: kill mid-batch (throw after commit, before ack) → restart → no duplicate rows.
