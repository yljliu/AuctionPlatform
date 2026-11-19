# 04 Live Updates: Tasks

- [x] 1. live.proto
  - `SubscribeAuction`, `GetAuctionSnapshot`, `AuctionEvent` (oneof), `AuctionSnapshot`, `BidAccepted`, `StateChanged`, `Heartbeat` per v2 §5.2; add `user_id` to `PublicBidRow.bidder`.
  - _Requirements: R1, R2.3_

- [ ] 2. PartitionStreamReader and AuctionEventBus
  - Background reader, entry → proto mapping, bounded per-subscriber channels with lag handling, `live_read_delay_ms`.
  - Test: XADD a fake entry → subscriber receives it; MaxRaised entry is not published.
  - _Requirements: R1.5, R2.1, R3.1_
  - Status: code and unit tests done; integration tests written, waiting for your run.

- [ ] 3. Snapshot builder and handle cache
  - `BuildSnapshot` from Redis (fallback SQL), `UserHandleCache`, masking.
  - Test: snapshot has no reserve or max fields.
  - _Requirements: R1.1, R1.5_
  - Status: code and unit tests done; integration tests written, waiting for your run.

- [ ] 4. SubscribeAuction with catch-up
  - Algorithm in design §2.3, heartbeat 15 s.
  - Tests: last_seen 0 → snapshot first; last_seen n → exactly n+1..cur then live; very old → snapshot; concurrent bids during catch-up → no gap, no repeat (assert seq sequence strictly +1).
  - _Requirements: R1.1–R1.4_
  - Status: code and unit tests done; integration tests written, waiting for your run.

- [ ] 5. Web LiveAuctionHub
  - Shared feed per auction, refcount, 30 s idle close, backoff reconnect with last seq, model apply.
  - Test (bUnit/xUnit with fake client): two watchers share one stream; stream error → status Reconnecting → resumes from last seq.
  - _Requirements: R4.1, R4.2, R4.4_
  - Status: code and unit tests done (`tests/Auction.Web.Tests`).

- [ ] 6. AuctionLive component and throttle
  - 100 ms render throttle, rows from model, "you" marking, bid button disabled while Reconnecting.
  - _Requirements: R4.3, R4.4_
  - Status: component, page `/a/{id}` and unit tests done.

- [ ] 7. Clock and countdown JS
  - `clock.js`, `ServerClock` EMA, red < 60 s, "Closing…" at 0.
  - _Requirements: R6.1, R6.2_
  - Status: code and unit tests done (`ServerClockTests`). Check by eye: the countdown on `/a/{id}`.

- [ ] 8. Reconnect banner
  - Custom reconnect modal; new circuit loads fresh snapshot.
  - Check by hand: stop web-2 container while viewing → banner → page recovers on another copy.
  - _Requirements: R5.1, R5.2_
  - Status: code done; waiting for your check by hand.

- [ ] 9. Latency check
  - Metrics from design §4; Playwright two-browser test across web-1 and web-2 (< 500 ms).
  - _Requirements: R3.1, R3.2_
  - Status: metrics done; browser test written (`tests/Auction.E2E.Tests`), waiting for your run.

## Build notes (differences from design.md)

- The stream reader polls every 20 ms when idle (StackExchange.Redis has no blocking XREAD).
- bid.lua now also writes `reserveMet` ('' / '1' / '0'), `minNextC` and `cur` to each stream entry, so live events need no extra Redis read. The reserve amount still never leaves the script.
- `PublicBidRow.bidder` is a `PublicUser`, which already has `user_id` (task 1 needed no change there).
- Snapshots read the state hash and the bid rows in one MULTI/EXEC, so the snapshot seq always matches its rows. `server_ts_ms` uses the Api clock.
- A snapshot of a Draft or Cancelled auction is `NOT_FOUND`, the same as the auction page.
- After the snapshot or the replayed events, the Api sends one heartbeat as a "caught up" mark. The Web feed turns Live on a snapshot or a heartbeat, so the bid button stays off during catch-up.
- The events ZSET entries from bid.lua now also hold `reserveMet`, `minNextC` and `atMs`, so replayed events match live ones.
- A lagged subscriber (queue full) gets a fresh snapshot, then live events again.
- The Web live client sends no user token (live updates are public); it is its own gRPC client without the auth interceptor.
- `AuctionLive` lives in `Components/Shared`; the page is `/a/{id}` as spec 06 plans, and spec 06 adds the rest of the page and the real bid panel. The button calls `OnBidRequested` with the min next bid.
- Web unit tests are in a new project, `tests/Auction.Web.Tests` (uses `Microsoft.Extensions.TimeProvider.Testing` for a fake clock).
- Clock offset: the first sample is used as is; later ones use `0.8*offset + 0.2*sample`. Starting from 0 would make the first countdowns wrong by most of the gap. The offset is sampled after a redraw whenever `server_ts_ms` moves on (snapshot, event or heartbeat).
- `clock.js` and `reconnect.js` are plain scripts (`window.auctionClock`), not JS modules, so the C# side needs no module handle.
- Blazor now starts from `reconnect.js` (`autostart="false"`): it retries every 2 s, 30 times, instead of every 20 s. When the server says the page connection is gone (`components-reconnect-rejected`, for example web-2 stopped), the page reloads by itself, gets a new connection on a working copy and loads a fresh snapshot. The banner covers the page, so nothing can be clicked while it shows.
- The latency test puts its own Live auction into Redis and bids through `bid.lua`, because auctions reach Redis only in spec 05 and the bid panel comes in spec 06. It skips unless `E2E_WEB1` is set. It counts bids (`#live-bid-count`), not the price, so number formats do not matter.
