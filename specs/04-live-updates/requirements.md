# 04 Live Updates: Requirements

Covers: `SubscribeAuction`, `GetAuctionSnapshot`, Web fan-out to browsers, reconnect and catch-up, server clock. Design refs: v2 sections 2.6, 2.8, W17, 5.2, 5.5.8.

## R1 Subscribe to an auction
1. WHEN a client calls `SubscribeAuction(auction_id, last_seen_seq = 0)` THE SYSTEM SHALL first send an `AuctionSnapshot` and then every later public event for that auction in `seq` order.
2. WHEN a client calls with `last_seen_seq = n` and events `n+1 .. current` are still kept THE SYSTEM SHALL send exactly those events, then live events, with no gap and no repeat.
3. IF events after `n` are no longer kept THEN THE SYSTEM SHALL send a snapshot instead.
4. THE SYSTEM SHALL send a `heartbeat` with `server_ts_ms` every 15 seconds.
5. THE SYSTEM SHALL never include any bidder's max or the reserve amount in a stream message.

## R2 Event content
1. WHEN a bid changes the price THE SYSTEM SHALL send `bid_accepted` with price, masked leader handle, bid count, `reserve_met`, end time, `extended`, and the new public rows.
2. WHEN an auction changes state (Opened, ClosingSoon, Sealed, Settled, Cancelled, Frozen, Resumed) THE SYSTEM SHALL send `state_changed` with the new status, and for Settled the outcome and final price.
3. EVERY event SHALL carry `seq` and `server_ts_ms`.

## R3 Speed
1. THE SYSTEM SHALL deliver a `bid_accepted` event from the Redis script to a subscribed Web copy within 150 ms at P99 under 500 bids/s.
2. THE SYSTEM SHALL update every browser viewing the auction within 500 ms at P99.

## R4 Web fan-out
1. THE SYSTEM SHALL open at most one `SubscribeAuction` stream per auction per Web copy, shared by all local viewers.
2. WHEN the last local viewer leaves THE SYSTEM SHALL close that stream within 30 seconds.
3. THE SYSTEM SHALL redraw a viewer's price at most 10 times per second; bid history rows SHALL never be dropped.
4. IF the Api stream breaks THEN THE SYSTEM SHALL reconnect with backoff (0.2 s doubling to 5 s) using the last seen seq, and the page SHALL show "Reconnecting… bidding paused" and disable the bid button until caught up.

## R5 Browser reconnect
1. WHEN the browser's SignalR circuit drops THE SYSTEM SHALL show the reconnect banner and disable bidding.
2. WHEN a new circuit starts (same or another Web copy) THE SYSTEM SHALL load a fresh snapshot.

## R6 Server clock
1. THE SYSTEM SHALL keep a per-browser clock offset from `server_ts_ms` (moving average, factor 0.2) and use it for countdowns.
2. THE SYSTEM SHALL show the countdown in red under 60 seconds and switch to "Closing…" at zero until `state_changed` arrives.
