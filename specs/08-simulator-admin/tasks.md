# 08 Simulator and Admin: Tasks

- [ ] 1. Admin helper RPCs
  - `AdminService.CreateBots(count)` and `CreateDemoAuctions(count, startsInSec, durationSec)`; admin only; bots `IsBot = 1`.
  - _Requirements: R1.1, R1.3_
  - Status: code done; integration tests written (`SimulationTests`), waiting for your run.

- [ ] 2. Simulator host and RunManager
  - `simulator.proto` (ListScenarios, StartRun, StopRun, AdjustRun, GetRun, ListRuns, StreamRunMetrics); single active run; `SimulationRuns` row.
  - Tests: second StartRun → `RUN_ALREADY_ACTIVE`; Stop stops bots within 2 s.
  - _Requirements: R3.1–R3.4_
  - Status: code and unit tests done (`SimulatorRunTests`: second start, stop within 2 s); `ops.SimulationRuns` saved (integration test written).

- [ ] 3. Bots and scheduler
  - Bot pool with shared channels and token refresh; per-auction token bucket; bid choice rule; seeded randomness.
  - _Requirements: R1.2, R2.4, R2.5_
  - Status: code and unit tests done (`SimulatorRulesTests`, `SimulatorRunTests`).

- [ ] 4. Scenarios
  - `steady`, `closing-spike`, `snipe-war` rate shapes; demo auction creation.
  - _Requirements: R2.1–R2.3_
  - Status: code and unit tests done (rate shapes in `SimulatorRulesTests`); demo auctions: integration test written.

- [ ] 5. Metrics stream
  - Counters, latency percentiles, rejected-by-code, extensions; 1 s push.
  - _Requirements: R3.5_
  - Status: code and unit tests done (`SimulatorRunTests`: counters and latency).

- [ ] 6. Verifier
  - Per design §2; watchers record final seq; report JSON saved.
  - Test: run `steady` 2 bots 30 s on 2 auctions → PASS. Inject a fault (delete one Bids row) → FAIL naming the auction.
  - _Requirements: R4.1–R4.7_
  - Status: code and unit tests done (`SimulatorVerifierTests`; `SimulatorRunTests` PASS and a dropped bid gives FAIL); integration tests on real SQL written (`SimulationTests`), waiting for your run.

- [ ] 7. GetSystemStats
  - Prometheus queries, Redis INFO, SQL count, health polling, fallbacks.
  - _Requirements: R5.1, R5.2_
  - Status: code and unit tests done (`SystemStatsTests`); the Prometheus numbers need the full stack to check.

- [ ] 8. Admin page simulator panel
  - Form, Start/Stop, live counters, event log tail, verifier chip and report dialog.
  - Check: `closing-spike` with 200 bots, 20 auctions, 5 min → PASS, P99 bid latency < 350 ms.
  - _Requirements: R5.3_
  - Status: code done; the check by hand is `scripts\sim-check.ps1`, waiting for your run.

## Build notes (differences from design.md)

- The simulator service listens on 7201 (gRPC, admin token only) and 7202 (health and metrics). It checks the admin's token with the same `JWT__SigningKey` as the Api. Prometheus now scrapes 7202.
- `CreateBots` signs bots in with `AuthManager.IssueForBotsAsync`, which refuses anyone who is not `IsBot` (no passwords, and never a person's account). Bots still bid only through the public `PlaceBid` with their own tokens, rate limits and idempotency keys.
- A name like `bot_0001` that belongs to a person makes `CreateBots` fail with `INVALID_STATE`; it never takes the account over.
- Bots use their own tokens for bids; the admin token is only for `CreateBots` and `CreateDemoAuctions`. A run uses the admin token it was started with, so a run longer than the token life (10 minutes) can no longer make new bots or auctions; bids go on, with the bots' refreshed tokens.
- Demo auctions start Scheduled 15 s after the run begins; the lifecycle leader loads and opens them. All end together. Each gets one drawn photo (the same SkiaSharp pictures as the seed).
- `closing-spike` and `snipe-war` end when every auction has closed (or 20 minutes after the planned length); `steady` ends after its duration. `snipe-war` makes auctions with a 30 s window, 15 s extension and a 300 s cap.
- The verifier replays each stream entry's bid (the row that carries the max) through `ProxyResolver`. It waits up to 2 minutes for ended auctions to settle and for every accepted bid to reach SQL, then reads SQL and the watchers' last seq once.
- `GetSystemStats` returns -1 for numbers it could not get (no Prometheus on the lite stack); the page shows "-". Online users come from the `circuits_active` gauge each web copy exports.
- The admin overview now polls every 2 s and draws bids per second from `GetSystemStats`. The spec 06 `GetStats` tiles (scheduled, waiting to settle, frozen, users) stay below the new ones.
- The lite stack also runs the simulator (port 7201), so the panel works there too; it has no Prometheus, so rates, P99 and online users show "-" there.
- `scripts\sim-check.ps1` is the task 8 check: it signs in as `admin@demo.local`, starts the run through the Simulator service, follows the metrics, and prints PASS only when the verifier passed and the last P99 was under 350 ms. Needs the Api on `localhost:7001` and the Simulator on `localhost:7201` (both are published in the compose files).
