# 05 Auction Lifecycle: Tasks

- [ ] 1. LeaseManager
  - Lease SQL from design §3, seed row in migration, renew loop, `IsLeader`, `FencingToken`, fencing-check helper.
  - Tests: two managers → one leader; stop renewing → other takes over with token + 1; stale token write throws.
  - _Requirements: R1.1–R1.3_

- [ ] 2. Lua state scripts
  - `load`, `open`, `soon`, `seal`, `settle`, `control`, `rebuild` per design §5, sharing one event-emit snippet.
  - Integration tests: each guard; open twice → one event; seal before end → `not_due` and re-score; seq/eseq +1 per event; events ZSET and stream entry written.
  - _Requirements: R2.3, R3.1, R4.1, R4.2, R6.1–R6.4_

- [ ] 3. LifecycleLoop A–D (load, open, soon, seal)
  - Tick per design §4 with SQL status updates (fenced).
  - Test: auction starting in 2 s opens within 1 s of start; ends and seals within 1 s; a late bid extension delays seal.
  - _Requirements: R2.1, R2.2, R3.1, R4.1–R4.3_

- [ ] 4. Settler and step E
  - Settlement transaction per design §6, drain check, `settlement_lag_seconds` metric, then `settle.lua`.
  - Tests: no bids → Unsold; below reserve → Unsold; Sold totals (premium 5%, shipping, seller fee 8%); crash after commit → rerun creates no second Settlement; not drained → waits.
  - _Requirements: R5.1–R5.7_

- [ ] 5. LiveAuctionControl and admin/seller actions
  - Implement interface; wire SellerService.CancelAuction (replace spec 02 stub) and AdminService ForceCancel/ForceClose/Freeze/Resume with AuditLog.
  - Tests: seller cancel races a bid → bid wins, cancel returns `INVALID_STATE`; freeze → bids get `AUCTION_FROZEN`; resume pushes end later.
  - _Requirements: R6.1–R6.5_

- [ ] 6. Recovery on leader start
  - Index rebuild and `rebuild.lua` path per design §7, Critical log.
  - Test: delete a Live auction's Redis keys, restart workers → auction is Frozen with correct price and leader; admin resume works.
  - _Requirements: R7.1, R7.2_

- [ ] 7. Leader failover check
  - Compose full: run 2 `workers`; `docker stop` the leader mid-auction; auction still seals and settles once.
  - _Requirements: R1.2, R5.7_
