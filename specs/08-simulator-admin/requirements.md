# 08 Simulator and Admin: Requirements

Covers: bot users, scenarios, live run metrics, verifier, admin simulator panel and system stats. Design refs: v2 W6, 5.5.13 (`GetSystemStats`), 5.5.14, 6.8, 7.2 invariants.

## R1 Bots
1. THE SYSTEM SHALL create or reuse bot users named `bot_0001…` with `IsBot = 1` and role Bidder.
2. Bots SHALL sign in and bid only through the public gRPC API, with real tokens, rate limits and idempotency keys.
3. Bot activity SHALL be excluded from seller analytics.

## R2 Scenarios
1. `steady`: N bots bid at a fixed total rate spread across the target auctions for the duration.
2. `closing-spike`: create M demo auctions ending together in K minutes; bots bid lightly until the last 60 s, then at the full rate.
3. `snipe-war`: create a few auctions; bots bid mostly inside the snipe window so extensions trigger until the cap.
4. Each bot SHALL choose amounts as current minimum + 0 to `aggressiveness × 5` increments, with a personal max ceiling, and SHALL stop bidding on an auction it leads.
5. A run SHALL accept an optional `seed` so runs are repeatable.

## R3 Control
1. WHEN an admin calls `StartRun` THE SYSTEM SHALL start the run and return its id; IF a run is active THEN it SHALL return `RUN_ALREADY_ACTIVE`.
2. WHEN an admin calls `StopRun` THE SYSTEM SHALL stop all bots within 2 seconds and then run the verifier.
3. WHEN an admin calls `AdjustRun` THE SYSTEM SHALL change bot count or rate within 2 seconds.
4. WHEN the duration ends THE SYSTEM SHALL stop and verify automatically.
5. `StreamRunMetrics` SHALL push every second: bids sent, accepted, rejected by code, P50/P99 latency, active bots, extensions.

## R4 Verifier
WHEN a run ends THE SYSTEM SHALL wait until all target auctions are Settled (or 2 minutes pass) and then check, per auction:
1. Every bid the bots saw accepted exists in `bidding.Bids` exactly once (matched by idempotency key).
2. Public `seq` values in `AuctionEvents` have no gaps.
3. Replaying accepted bids in eseq order through `ProxyResolver` gives the final price and leader stored in SQL.
4. The price never decreases across events.
5. Total extension used ≤ `MaxTotalExtensionSec`.
6. No auction remains Sealed.
7. Every live subscriber the simulator kept (one per auction) received the same final seq as SQL.
THE SYSTEM SHALL store the result as PASS or FAIL with counts and failing auction ids in `ops.SimulationRuns.Report`.

## R5 Admin page
1. THE SYSTEM SHALL show tiles: bids/s, live auctions, online users, persist lag, error rate, P99 bid latency, Redis memory %, replica status.
2. THE SYSTEM SHALL show service health (healthy/total per service).
3. THE SYSTEM SHALL show the simulator panel: scenario, bots, rate, targets, duration, Start/Stop, live counters, event log tail (last 50 events), and the last verifier result with a Report link.
