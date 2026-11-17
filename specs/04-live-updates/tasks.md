# 04 Live Updates: Tasks

- [ ] 1. live.proto
  - `SubscribeAuction`, `GetAuctionSnapshot`, `AuctionEvent` (oneof), `AuctionSnapshot`, `BidAccepted`, `StateChanged`, `Heartbeat` per v2 §5.2; add `user_id` to `PublicBidRow.bidder`.
  - _Requirements: R1, R2.3_

- [ ] 2. PartitionStreamReader and AuctionEventBus
  - Background reader, entry → proto mapping, bounded per-subscriber channels with lag handling, `live_read_delay_ms`.
  - Test: XADD a fake entry → subscriber receives it; MaxRaised entry is not published.
  - _Requirements: R1.5, R2.1, R3.1_

- [ ] 3. Snapshot builder and handle cache
  - `BuildSnapshot` from Redis (fallback SQL), `UserHandleCache`, masking.
  - Test: snapshot has no reserve or max fields.
  - _Requirements: R1.1, R1.5_

- [ ] 4. SubscribeAuction with catch-up
  - Algorithm in design §2.3, heartbeat 15 s.
  - Tests: last_seen 0 → snapshot first; last_seen n → exactly n+1..cur then live; very old → snapshot; concurrent bids during catch-up → no gap, no repeat (assert seq sequence strictly +1).
  - _Requirements: R1.1–R1.4_

- [ ] 5. Web LiveAuctionHub
  - Shared feed per auction, refcount, 30 s idle close, backoff reconnect with last seq, model apply.
  - Test (bUnit/xUnit with fake client): two watchers share one stream; stream error → status Reconnecting → resumes from last seq.
  - _Requirements: R4.1, R4.2, R4.4_

- [ ] 6. AuctionLive component and throttle
  - 100 ms render throttle, rows from model, "you" marking, bid button disabled while Reconnecting.
  - _Requirements: R4.3, R4.4_

- [ ] 7. Clock and countdown JS
  - `clock.js`, `ServerClock` EMA, red < 60 s, "Closing…" at 0.
  - _Requirements: R6.1, R6.2_

- [ ] 8. Reconnect banner
  - Custom reconnect modal; new circuit loads fresh snapshot.
  - Check by hand: stop web-2 container while viewing → banner → page recovers on another copy.
  - _Requirements: R5.1, R5.2_

- [ ] 9. Latency check
  - Metrics from design §4; Playwright two-browser test across web-1 and web-2 (< 500 ms).
  - _Requirements: R3.1, R3.2_
