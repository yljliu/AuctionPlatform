# 05 Auction Lifecycle: Tasks

- [ ] 1. LeaseManager
  - Lease SQL from design §3, seed row in migration, renew loop, `IsLeader`, `FencingToken`, fencing-check helper.
  - Tests: two managers → one leader; stop renewing → other takes over with token + 1; stale token write throws.
  - _Requirements: R1.1–R1.3_
  - Status: code and unit tests done; integration tests written, waiting for your run.

- [ ] 2. Lua state scripts
  - `load`, `open`, `soon`, `seal`, `settle`, `control`, `rebuild` per design §5, sharing one event-emit snippet.
  - Integration tests: each guard; open twice → one event; seal before end → `not_due` and re-score; seq/eseq +1 per event; events ZSET and stream entry written.
  - _Requirements: R2.3, R3.1, R4.1, R4.2, R6.1–R6.4_
  - Status: scripts and unit tests done; integration tests written, waiting for your run.

- [ ] 3. LifecycleLoop A–D (load, open, soon, seal)
  - Tick per design §4 with SQL status updates (fenced).
  - Test: auction starting in 2 s opens within 1 s of start; ends and seals within 1 s; a late bid extension delays seal.
  - _Requirements: R2.1, R2.2, R3.1, R4.1–R4.3_
  - Status: code and unit tests done; integration tests written, waiting for your run.

- [ ] 4. Settler and step E
  - Settlement transaction per design §6, drain check, `settlement_lag_seconds` metric, then `settle.lua`.
  - Tests: no bids → Unsold; below reserve → Unsold; Sold totals (premium 5%, shipping, seller fee 8%); crash after commit → rerun creates no second Settlement; not drained → waits.
  - _Requirements: R5.1–R5.7_
  - Status: code and unit tests done; integration tests written, waiting for your run.

- [ ] 5. LiveAuctionControl and admin/seller actions
  - Implement interface; wire SellerService.CancelAuction (replace spec 02 stub) and AdminService ForceCancel/ForceClose/Freeze/Resume with AuditLog.
  - Tests: seller cancel races a bid → bid wins, cancel returns `INVALID_STATE`; freeze → bids get `AUCTION_FROZEN`; resume pushes end later.
  - _Requirements: R6.1–R6.5_
  - Status: code and unit tests done; integration tests written, waiting for your run.

- [ ] 6. Recovery on leader start
  - Index rebuild and `rebuild.lua` path per design §7, Critical log.
  - Test: delete a Live auction's Redis keys, restart workers → auction is Frozen with correct price and leader; admin resume works.
  - _Requirements: R7.1, R7.2_
  - Status: code and unit tests done; integration tests written, waiting for your run.

- [ ] 7. Leader failover check
  - Compose full: run 2 `workers`; `docker stop` the leader mid-auction; auction still seals and settles once.
  - _Requirements: R1.2, R5.7_
  - Status: check written (`scripts/failover-check.ps1`, Simulator `failover-check`); waiting for your run on the full stack.

## Build notes (differences from design.md)

- The lease SQL sits behind `ILeaseStore` (`SqlLeaseStore`), so `LeaseManager` rules have unit tests with a fake store and clock.
- `IsLeader` also turns false 1 s before the lease could expire in SQL, if no renew has worked by then (clock drift guard).
- On a clean stop the leader releases the lease (sets `ExpiresAt` to the past, keeps `Holder`), so another copy takes over at once with token + 1.
- The holder name is `INSTANCE_NAME:<new guid>`, so a restarted copy is a new holder and gets a new token.
- Leader work takes `LeaseManager.Current` (a `LeaseGrant`) once and passes it to `Fencing.EnsureAsync`, which throws `StaleFencingTokenException`.
- `BidSchemaModelTests.BidEseq_is_the_latest_migration` became `BidEseq_migration_runs_after_Initial`, since spec 05 adds a newer migration.
- The shared event code is `state_emit.lua`. A script pulls it in with a line `--@include state_emit.lua`; `LuaScripts.Load` pastes the text in, so Redis still gets one whole script.
- Every lifecycle script takes the same nine keys (state, events, stream, starts, ends, soon, sealed, max, bids); `LifecycleScripts.KeysFor` builds them.
- State events now carry `change` (Opened, ClosingSoon, Sealed, Settled, Cancelled, Frozen, Resumed) next to `status` (the auction status after the change). ClosingSoon and Resumed keep status Live, so the bid button stays on.
- `StateChanged` in live.proto got `change = 5` and `winner = 6` (masked handle), so R5.6 can show the winner. The persister stores change, outcome, finalC and winner in the State payload.
- `open`, `soon` and `seal` re-score their index entry when it is not time yet, and drop it when the status is wrong.
- An admin force-cancel also works on a Frozen auction (for example one rebuilt after Redis loss). A seller cancel still needs Live or Scheduled and no bids.
- `freeze.lua` stays as the persister's emergency freeze (any status but Frozen) and now uses the shared emit code. `control.lua freeze` is the admin one (Live only).
- `rebuild.lua` sets `persistedEseq = eseq` from SQL, then its Frozen event takes the next seq and eseq.
- Step A (load) asks SQL once a second, not every 250 ms tick; steps B to D still run every tick. With a 5 minute look-ahead this changes nothing for opening times.
- If Redis says an auction already moved on (Live, Sealed or Frozen) but SQL still says Scheduled, the load step updates SQL (fenced). This covers a lost SQL update after `open.lua`.
- A failure on one auction is logged and retried next tick; it never stops the others. A stale fencing token stops the whole tick at once.
- `LifecycleWorker` ticks only while `LeaseManager.Current` is set, and calls `LifecycleLoop.Reset` when the lease is lost.
- `LifecycleOptions.OnlyPartitions` and `KeysFor` are for tests only, so tests use their own partition and never meet a running workers copy on the same Redis.
- Settlement math is pure (`Auction.Domain/Settlements/SettlementMath.cs`): halves round away from zero; shipping counts only when the buyer pays.
- `Settler` uses Dapper in one SERIALIZABLE transaction and writes the outbox row with SQL (same transaction), not through EF.
- The settler also accepts an auction that is still Live in SQL (the SQL update after seal may have been lost); Redis decides that it is Sealed. A Cancelled or missing auction is not settled and is not retried until the next leader start.
- `settlement_lag_seconds` is a gauge of the longest wait among Sealed auctions this tick; `auctions_settled` counts settlements. Both are on the Workers `/metrics`.
- `ILiveAuctionControl` now has Cancel (with force), Close, Freeze and Resume, each returning Done, NotInRedis or InvalidState. `RedisLiveAuctionControl` replaces the spec 02 stub in DI; the stub stays for tests.
- Seller cancel now also asks Redis for a Scheduled auction, because one starting within 5 minutes is already loaded there. Without this, Redis could open an auction SQL had cancelled.
- Admin actions are new RPCs on `AdminService` (`ForceCancelAuction`, `ForceCloseAuction`, `FreezeAuction`, `ResumeAuction`, all with `AuctionActionRequest`). Every one writes an AuditLog row (`auction.force_cancel`, `auction.force_close`, `auction.freeze`, `auction.resume`), also force-close.
- Admin SQL changes use a status-checked `ExecuteUpdate`, not the row version, because the persister writes the same auction row while bids come in. Resume also moves SQL `EndTime` to the new Redis end.
- Resume needs the Redis copy; Freeze and Force-cancel also work when the auction is not in Redis.
- Recovery runs once each time a copy becomes leader, before its first tick. It also rebuilds Frozen auctions whose Redis state is gone, and settles Sealed auctions whose Redis state is gone straight from SQL. A rebuilt Live auction is set Frozen in SQL too, so admin resume works.
- The admin alert for a rebuild is a Critical log `AuctionRebuiltFrozen` (spec 08 can turn it into a dashboard alert).
- The failover check is a Simulator command run from the host. It makes its own Scheduled auction (seller1, 15 s to start, 60 s long), bids through bid.lua, runs `docker compose stop` (or `kill`) on the lease holder, then checks: new holder, higher token, one Sealed and one Settled event, one Settlement. It starts the stopped copy again at the end.
