# 08 Simulator and Admin: Design

## 1. Simulator service (`Auction.Simulator`)

ASP.NET Core host exposing `SimulatorService` (gRPC, admin JWT validated with the same key as Api). Web's admin page calls it directly (`simulator:7201`).

```
RunManager (singleton): at most one active Run
Run
  Config: scenario, bots, ratePerAuction, targets[], createDemo, demoMinutes, durationSec, aggressiveness, seed
  BotPool: N Bot objects sharing one GrpcChannel per api endpoint (HTTP/2 multiplexing; ~10 channels total)
  Scheduler: token-bucket per auction → picks a random eligible bot → bot.TryBid(auction)
  Watchers: one SubscribeAuction stream per target auction (records seq and live delay)
  Metrics: counters + HdrHistogram-style latency (simple sorted reservoir is fine)
  Ledger: ConcurrentBag<(auctionId, idemKey, outcome, seq)> for the verifier
```

Bot sign-in: `EnsureBotsAsync(n)` registers missing bots via an internal admin RPC `AdminService.CreateBots(count)` (sets `IsBot`, skips rate limits for registration only), then logs in each bot once and refreshes tokens as needed.

Bot bid choice:
```
min = state.MinNext; inc = IncrementFor(state.Price)
amount = min + rnd(0 .. aggressiveness*5) * inc
if amount > bot.Ceiling(auction) → skip
if state.Leader == bot → skip
```
`bot.Ceiling` = starting × U(3, 20) per auction (seeded).

Demo auctions: `AdminService.CreateDemoAuctions(count, startsInSec, durationSec, seller)` creates items + auctions owned by `seller1` with a placeholder image.

Scenario shapes:
| Scenario | Rate over time |
|---|---|
| steady | constant |
| closing-spike | 10% of rate until end − 60 s, then 100% (auctions share one end time) |
| snipe-war | 5% until end − snipe window, then 100%; continues through extensions |

## 2. Verifier

Runs in the simulator after the run (queries SQL read-only with a dedicated connection string):
```sql
SELECT ESeq, Idx, Kind, UserId, Amount, MaxAmount, IdempotencyKey FROM bidding.Bids WHERE AuctionId=@id ORDER BY ESeq, Idx;
SELECT Seq, EventType, Payload FROM bidding.AuctionEvents WHERE AuctionId=@id AND Seq IS NOT NULL ORDER BY Seq;
SELECT CurrentPrice, CurrentLeaderId, Status, EndTime, ScheduledEndTime, MaxTotalExtensionSec FROM catalog.Auctions WHERE AuctionId=@id;
```
Checks R4.1–R4.7. Replay uses `ProxyResolver` from `Auction.Domain` (the simulator references Domain). Result JSON:
```json
{ "result":"PASS", "auctions":50, "bidsSent":41220, "accepted":38902, "missingInSql":0, "duplicates":0,
  "seqGaps":0, "replayMismatches":0, "priceDecreases":0, "extensionOverCap":0, "stuckSealed":0,
  "subscriberSeqMismatch":0, "failingAuctions":[] }
```

## 3. System stats (`AdminService.GetSystemStats`)

Api computes from:
- Prometheus HTTP API (`/api/v1/query`) for bids/s, error rate, P99 latency, persist lag (simple queries, 15 s cache).
- Redis `INFO memory`, `INFO replication` (connected_slaves).
- SQL: count Live auctions.
- Online users: Web reports `circuits_active` gauge; sum across web copies from Prometheus.
- Health: Api polls each service's `/health/ready` (list from config) every 5 s.

Fallback if Prometheus is down: return Redis/SQL numbers and leave others null (UI shows "—").

## 4. Admin page (Web)
- `Overview.razor` polls `GetSystemStats` every 2 s (timer) and keeps the last 15 min in memory for `MudChart`.
- Simulator panel: form → `StartRun`; while active, `StreamRunMetrics` drives counters; event log tail comes from a `SubscribeAuction` on one target (simple) or the run's own event list.
- Verifier result chip PASS (green) / FAIL (red) with a dialog showing the JSON.
