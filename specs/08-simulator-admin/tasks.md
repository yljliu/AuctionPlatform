# 08 Simulator and Admin: Tasks

- [ ] 1. Admin helper RPCs
  - `AdminService.CreateBots(count)` and `CreateDemoAuctions(count, startsInSec, durationSec)`; admin only; bots `IsBot = 1`.
  - _Requirements: R1.1, R1.3_

- [ ] 2. Simulator host and RunManager
  - `simulator.proto` (ListScenarios, StartRun, StopRun, AdjustRun, GetRun, ListRuns, StreamRunMetrics); single active run; `SimulationRuns` row.
  - Tests: second StartRun → `RUN_ALREADY_ACTIVE`; Stop stops bots within 2 s.
  - _Requirements: R3.1–R3.4_

- [ ] 3. Bots and scheduler
  - Bot pool with shared channels and token refresh; per-auction token bucket; bid choice rule; seeded randomness.
  - _Requirements: R1.2, R2.4, R2.5_

- [ ] 4. Scenarios
  - `steady`, `closing-spike`, `snipe-war` rate shapes; demo auction creation.
  - _Requirements: R2.1–R2.3_

- [ ] 5. Metrics stream
  - Counters, latency percentiles, rejected-by-code, extensions; 1 s push.
  - _Requirements: R3.5_

- [ ] 6. Verifier
  - Per design §2; watchers record final seq; report JSON saved.
  - Test: run `steady` 2 bots 30 s on 2 auctions → PASS. Inject a fault (delete one Bids row) → FAIL naming the auction.
  - _Requirements: R4.1–R4.7_

- [ ] 7. GetSystemStats
  - Prometheus queries, Redis INFO, SQL count, health polling, fallbacks.
  - _Requirements: R5.1, R5.2_

- [ ] 8. Admin page simulator panel
  - Form, Start/Stop, live counters, event log tail, verifier chip and report dialog.
  - Check: `closing-spike` with 200 bots, 20 auctions, 5 min → PASS, P99 bid latency < 350 ms.
  - _Requirements: R5.3_
