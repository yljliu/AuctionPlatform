# 05 Auction Lifecycle: Design

## 1. Parts

| Part | Where | Job |
|---|---|---|
| `LeaseManager` | Workers | Acquire/renew lease, expose `IsLeader`, `FencingToken` |
| `LifecycleLoop` | Workers | 250 ms tick when leader: load, open, closing-soon, seal, settle |
| `Settler` | Workers | SQL settlement transaction |
| `LiveAuctionControl` | Infrastructure | Implements `ILiveAuctionControl` (cancel, force-cancel, force-close, freeze, resume) used by Api |
| Lua scripts | Infrastructure | `load.lua`, `open.lua`, `soon.lua`, `seal.lua`, `settle.lua`, `control.lua`, `rebuild.lua` |

## 2. Partition index keys (same `{pN}` tag as spec 03)

| Key | Type | Score |
|---|---|---|
| `{pN}:starts` | ZSET | startMs of Scheduled auctions loaded into Redis |
| `{pN}:ends` | ZSET | endMs of Live auctions (may be stale after extensions; `seal.lua` re-scores) |
| `{pN}:soon` | ZSET | endMs − 60 s, for closing-soon |
| `{pN}:sealed` | ZSET | sealedAtMs |

## 3. Lease

```sql
-- acquire or renew (one statement, returns token)
UPDATE bidding.LifecycleLeases WITH (UPDLOCK, HOLDLOCK)
SET Holder = @me,
    FencingToken = CASE WHEN Holder = @me THEN FencingToken ELSE FencingToken + 1 END,
    ExpiresAt = DATEADD(SECOND, 15, SYSUTCDATETIME())
OUTPUT inserted.FencingToken
WHERE LeaseName = 'lifecycle' AND (Holder = @me OR ExpiresAt < SYSUTCDATETIME());
```
Seed one row `('lifecycle', '', 0, '2000-01-01')`. No row returned → not leader. Renew every 5 s; if renew fails, stop work immediately (`IsLeader = false`).

Fencing check inside every SQL write the leader makes:
```sql
IF NOT EXISTS (SELECT 1 FROM bidding.LifecycleLeases WITH (UPDLOCK)
               WHERE LeaseName='lifecycle' AND Holder=@me AND FencingToken=@token AND ExpiresAt > SYSUTCDATETIME())
  THROW 50001, 'STALE_FENCING_TOKEN', 1;
```
Redis Lua steps are guarded by status checks, so a stale leader running them is harmless.

## 4. Tick

```
every 250 ms if IsLeader:
  A. load:   SQL: Scheduled AND StartTime <= now+5min AND not in loadedSet (memory)  → load.lua (HSETNX-style: only if state missing) ; ZADD starts
  B. open:   ZRANGEBYSCORE starts -inf nowMs LIMIT 100 → open.lua each
             on opened → SQL UPDATE Status=Live (fenced)
  C. soon:   ZRANGEBYSCORE soon -inf nowMs LIMIT 100 → soon.lua each
  D. seal:   ZRANGEBYSCORE ends -inf nowMs LIMIT 100 → seal.lua each
             on sealed → SQL UPDATE Status=Sealed (fenced)
  E. settle: ZRANGE sealed 0 99 → for each: HMGET state eseq persistedEseq
             if persistedEseq >= eseq → Settler.Settle(id) → settle.lua
             elif now − sealedAt > 30 s → metric settlement_lag_seconds, warn (once per 30 s)
```
Use Redis `TIME` (one call per tick) for `nowMs`.

## 5. Lua scripts (state transitions)

All emit through the same helper code (inlined): `seq += 1; eseq += 1; ZADD events seq json; XADD stream * type State auction id status S seq seq eseq eseq at nowMs ...`.

| Script | Guard | Changes | Event |
|---|---|---|---|
| `load.lua` | state missing | create hash (all fields from spec 03 §2), status Scheduled, seq 0, eseq 0, persistedEseq 0 | none |
| `open.lua` | status Scheduled and now ≥ startMs | status Live; ZREM starts; ZADD ends endMs; ZADD soon endMs−60000 | Opened |
| `soon.lua` | status Live and `soonSent` empty | `soonSent = 1`; ZREM soon | ClosingSoon (status stays Live) |
| `seal.lua` | status Live | if now ≥ endMs: status Sealed, sealedAtMs, ZREM ends, ZADD sealed; else ZADD ends endMs (re-score) and return `not_due` | Sealed |
| `settle.lua` | status Sealed | status Settled, outcome, finalC, winner; ZREM sealed; EXPIRE all auction keys 600 | Settled |
| `control.lua` op=cancel | status Live/Scheduled and (force or bidCount == 0) | status Cancelled; ZREM from all index sets; EXPIRE 600 | Cancelled |
| `control.lua` op=close | status Live | endMs = now; ZADD ends now | none (seal follows) |
| `control.lua` op=freeze | status Live | status Frozen, frozenAtMs | Frozen |
| `control.lua` op=resume | status Frozen | status Live, endMs += now − frozenAtMs (min end = now + 60 s), ZADD ends, ZADD soon | Resumed |
| `rebuild.lua` | state missing | create hash from SQL values with status Frozen | Frozen |

The persister (spec 03) writes `State` entries to `bidding.AuctionEvents` and advances `persistedEseq`; it never changes `Auctions.Status`.

## 6. Settler

```
BEGIN TRAN (SERIALIZABLE on the auction row)
  fencing check
  a = SELECT ... FROM catalog.Auctions WITH (UPDLOCK) WHERE AuctionId=@id
  if a.Status == Settled: COMMIT; return existing outcome         -- idempotent
  if a.BidCount == 0               → Unsold
  elif a.ReservePrice != null and a.CurrentPrice < a.ReservePrice → Unsold
  else                              → Sold to a.CurrentLeaderId at a.CurrentPrice
  if Sold:
     fee = FeeSchedules[a.FeeScheduleId]
     premium = round(price * fee.BuyerPremiumPct / 100, 2)
     shipping = item.ShippingType == BuyerPays ? item.ShippingCost : 0
     total = price + premium + shipping              -- tax 0 in MVP
     sellerFee = round(price * fee.SellerFinalValuePct / 100, 2)
     INSERT pay.Settlements (... Status=PendingPayment, PaymentDeadline = now+72h)
       -- unique filtered index UX_Settlement_Active blocks a second one
  UPDATE catalog.Auctions SET Status=Settled, Outcome=@o
  UPDATE catalog.Items SET Status = Sold|Unsold
  OutboxWriter.Add("AuctionSettled", {auctionId, outcome, price, winnerId, sellerId, settlementId})
COMMIT
then settle.lua
```
Crash between commit and `settle.lua`: next tick sees Redis still Sealed, calls Settler again, which returns the existing outcome (idempotent), then runs `settle.lua`.

## 7. Recovery on leader start
1. SQL: Scheduled with StartTime ≤ now + 5 min → `load.lua` + ZADD starts.
2. SQL: Live → if state exists: ZADD ends/soon from Redis endMs; else `rebuild.lua` (price, leader, leaderMax = MAX(MaxAmount) per user from Bids, bidCount, seq = LastSeq, eseq = persistedEseq = LastESeq, endMs = EndTime) and log Critical `AuctionRebuiltFrozen`.
3. SQL: Sealed → ZADD sealed.
For rebuild, also refill `{pN}:a:{id}:max` from Bids (latest MaxAmount per user).

## 8. Api use
`AdminService.ForceCancelAuction / ForceCloseAuction / FreezeAuction / ResumeAuction` and `SellerService.CancelAuction` call `ILiveAuctionControl`, then update SQL status and write `AuditLog` (cancel/freeze/resume). If the Lua guard fails, return `INVALID_STATE`.
