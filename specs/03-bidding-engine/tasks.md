# 03 Bidding Engine: Tasks

- [x] 1. IncrementTable and MinNext
  - Domain class + tests at each tier boundary; loader from `catalog.BidIncrementTiers` and serializer to the `inc` string.
  - _Requirements: R3.1, R3.2_

- [x] 2. ProxyResolver with test vectors
  - Implement design Ãƒâ€šÃ‚Â§3. Create `tests/vectors/proxy.json` (ÃƒÂ¢Ã¢â‚¬Â°Ã‚Â¥ 25 cases). Table test + FsCheck properties.
  - _Requirements: R4.1ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Å“R4.5, R4.7_

- [x] 3. Redis key helper and partitioning
  - `RedisKeys.For(auctionId)` returning all keys with `{pN}` tag; `Partitions` from config; unit test stable mapping.
  - _Requirements: R7.1_

- [ ] 4. bid.lua
  - Write script per design Ãƒâ€šÃ‚Â§4 in `Infrastructure/Redis/Scripts/bid.lua` (embedded resource). `ScriptRegistry` loads and calls with NOSCRIPT retry.
  - Integration tests (Testcontainers Redis): every vector matches `ProxyResolver`; NOT_LIVE, ENDED, OWN_LOT, FROZEN; anti-sniping extends and respects cap; MaxRaised does not extend; idempotent replay returns same outcome; mismatch.
  - _Requirements: R2.1ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Å“R2.3, R4, R5.1ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Å“R5.3, R6.1, R6.2, R7.1, R7.2, R8.2_
  - Status: code and unit tests done; integration tests written, waiting for your run.

- [ ] 5. Concurrency test
  - 50 parallel bidders ÃƒÆ’Ã¢â‚¬â€ 20 bids; assert seq gapless and replay equality.
  - _Requirements: R7.1, R7.2_
  - Status: code and unit tests done; integration tests written, waiting for your run.

- [ ] 6. BiddingService.PlaceBid
  - `bidding.proto` (PlaceBid, GetBidHistory, GetMyBidStatus, ListMyBids). Implement PlaceBid per design Ãƒâ€šÃ‚Â§5 incl. rate limit, user status, validation, WAIT with `Redis:RequireReplicaAck` flag, metrics.
  - Integration tests: banned ÃƒÂ¢Ã¢â‚¬Â Ã¢â‚¬â„¢ `ACCOUNT_BANNED`; 6 bids in 1 s ÃƒÂ¢Ã¢â‚¬Â Ã¢â‚¬â„¢ `RATE_LIMITED`; 3 decimals ÃƒÂ¢Ã¢â‚¬Â Ã¢â‚¬â„¢ `VALIDATION_FAILED`; replica down + flag on ÃƒÂ¢Ã¢â‚¬Â Ã¢â‚¬â„¢ `SERVICE_UNAVAILABLE`, retry after replica up returns stored success.
  - _Requirements: R1.1ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Å“R1.4, R3.3, R3.4, R8.1_
  - Status: code and unit tests done; integration tests written, waiting for your run.

- [ ] 7. Schema migration for bids
  - Migration `0002_BidEseq`: `Bids.ESeq`, `Bids.Idx`, new unique index, `Kind` MaxRaise, `Auctions.LastESeq`, `AuctionEvents.ESeq`.
  - _Requirements: R8.3_
  - Status: code and unit tests done; integration tests written, waiting for your run.

- [ ] 8. Persister worker
  - Implement design Ãƒâ€šÃ‚Â§6: group create, pending replay, XAUTOCLAIM, batch transaction, `persistedEseq`, ack, trim, backoff, DLQ + freeze (use a `freeze.lua` stub that sets status Frozen and emits a state event; spec 05 owns the full version).
  - Tests: entries land in SQL in order; replay after crash-before-ack makes no duplicates; SQL down ÃƒÂ¢Ã¢â‚¬Â Ã¢â‚¬â„¢ no ack, recovers after SQL returns; poison entry ÃƒÂ¢Ã¢â‚¬Â Ã¢â‚¬â„¢ DLQ and auction Frozen.
  - _Requirements: R8.3ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Å“R8.5, R9.1_
  - Status: code and unit tests done; integration tests written, waiting for your run.

- [ ] 9. Persist lag metric and load check
  - `persist_lag_ms` gauge. Quick load test: 500 bids/s for 60 s across 20 auctions (simple console or simulator stub) ÃƒÂ¢Ã¢â‚¬Â Ã¢â‚¬â„¢ lag stays < 2 s.
  - _Requirements: R8.6_
  - Status: gauge and unit tests done; load check is `dotnet run --project src/Auction.Simulator -- load-check`, waiting for your run.

- [ ] 10. Read endpoints
  - GetBidHistory (Redis page 1, SQL beyond), GetMyBidStatus, ListMyBids with masking.
  - Test: other users see masked handles; the bidder sees their own name; no max values in history.
  - _Requirements: R4.6, R10.1ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Å“R10.3_
  - Status: code and unit tests done; integration tests written, waiting for your run.

## Build notes (differences from design.md)

- Migration is `<timestamp>_BidEseq` (EF names by time). `Bids.SubSeq` is dropped; `Bids.Seq` is nullable (null for MaxRaise rows); unique index is `UX_Bids_ESeq (AuctionId, ESeq, Idx)`.
- bid.lua also writes `bidCount` into the stream entry, so the persister can set `Auctions.BidCount`.
- The persister polls every 50 ms when idle (StackExchange.Redis has no blocking XREADGROUP).
- An entry that cannot be read (bad fields) goes to the DLQ at once: no retry can fix it.
- SQL down is not counted against entries: the persister backs off (100 ms doubling to 5 s) and retries. Only when SQL is up and one entry still fails is it counted (10 tries, then DLQ + freeze).
- When a batch fails with SQL up, entries are written one at a time; later entries of a failing auction wait, so each auction stays in order.
- Load check (task 9) is a Simulator command; the auctions exist only in Redis.
