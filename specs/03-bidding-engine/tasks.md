# 03 Bidding Engine: Tasks

- [ ] 1. IncrementTable and MinNext
  - Domain class + tests at each tier boundary; loader from `catalog.BidIncrementTiers` and serializer to the `inc` string.
  - _Requirements: R3.1, R3.2_

- [ ] 2. ProxyResolver with test vectors
  - Implement design §3. Create `tests/vectors/proxy.json` (≥ 25 cases). Table test + FsCheck properties.
  - _Requirements: R4.1–R4.5, R4.7_

- [ ] 3. Redis key helper and partitioning
  - `RedisKeys.For(auctionId)` returning all keys with `{pN}` tag; `Partitions` from config; unit test stable mapping.
  - _Requirements: R7.1_

- [ ] 4. bid.lua
  - Write script per design §4 in `Infrastructure/Redis/Scripts/bid.lua` (embedded resource). `ScriptRegistry` loads and calls with NOSCRIPT retry.
  - Integration tests (Testcontainers Redis): every vector matches `ProxyResolver`; NOT_LIVE, ENDED, OWN_LOT, FROZEN; anti-sniping extends and respects cap; MaxRaised does not extend; idempotent replay returns same outcome; mismatch.
  - _Requirements: R2.1–R2.3, R4, R5.1–R5.3, R6.1, R6.2, R7.1, R7.2, R8.2_

- [ ] 5. Concurrency test
  - 50 parallel bidders × 20 bids; assert seq gapless and replay equality.
  - _Requirements: R7.1, R7.2_

- [ ] 6. BiddingService.PlaceBid
  - `bidding.proto` (PlaceBid, GetBidHistory, GetMyBidStatus, ListMyBids). Implement PlaceBid per design §5 incl. rate limit, user status, validation, WAIT with `Redis:RequireReplicaAck` flag, metrics.
  - Integration tests: banned → `ACCOUNT_BANNED`; 6 bids in 1 s → `RATE_LIMITED`; 3 decimals → `VALIDATION_FAILED`; replica down + flag on → `SERVICE_UNAVAILABLE`, retry after replica up returns stored success.
  - _Requirements: R1.1–R1.4, R3.3, R3.4, R8.1_

- [ ] 7. Schema migration for bids
  - Migration `0002_BidEseq`: `Bids.ESeq`, `Bids.Idx`, new unique index, `Kind` MaxRaise, `Auctions.LastESeq`, `AuctionEvents.ESeq`.
  - _Requirements: R8.3_

- [ ] 8. Persister worker
  - Implement design §6: group create, pending replay, XAUTOCLAIM, batch transaction, `persistedEseq`, ack, trim, backoff, DLQ + freeze (use a `freeze.lua` stub that sets status Frozen and emits a state event; spec 05 owns the full version).
  - Tests: entries land in SQL in order; replay after crash-before-ack makes no duplicates; SQL down → no ack, recovers after SQL returns; poison entry → DLQ and auction Frozen.
  - _Requirements: R8.3–R8.5, R9.1_

- [ ] 9. Persist lag metric and load check
  - `persist_lag_ms` gauge. Quick load test: 500 bids/s for 60 s across 20 auctions (simple console or simulator stub) → lag stays < 2 s.
  - _Requirements: R8.6_

- [ ] 10. Read endpoints
  - GetBidHistory (Redis page 1, SQL beyond), GetMyBidStatus, ListMyBids with masking.
  - Test: other users see masked handles; the bidder sees their own name; no max values in history.
  - _Requirements: R4.6, R10.1–R10.3_
