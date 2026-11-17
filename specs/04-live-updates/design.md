# 04 Live Updates: Design

## 1. Flow

```
bid.lua / lifecycle Lua
   ├─ ZADD {pN}:a:{id}:events seq json      (kept 200)
   └─ XADD {pN}:stream ...                   (all entries)
            │  XREAD BLOCK (no group; every Api copy reads everything)
            ▼
Api: PartitionStreamReader (1 per partition) → AuctionEventBus (in-process)
            │  per-auction subscribers (Channel<AuctionEvent>)
            ▼
Api: LiveGrpcService.SubscribeAuction  ── gRPC server stream ──▶ Web: LiveAuctionHub (1 stream / auction / Web copy)
                                                                     │ C# events, refcounted
                                                                     ▼
                                                     Blazor components (per circuit) → SignalR → browser
```

## 2. Api side

### 2.1 PartitionStreamReader (`BackgroundService`)
- Starts at `$` (only new entries). Loop: `XREAD COUNT 500 BLOCK 5000 STREAMS {pN}:stream <lastId>`.
- For entries with a public `seq` (type `Bid` without `MaxRaised`, or type `State`): map to `AuctionEvent` proto and publish to `AuctionEventBus`.
- Skips `MaxRaised` and anything private.
- Records `live_read_delay_ms = now − entry.at`.
- On Redis error: log, wait 1 s, resume from `lastId`.

### 2.2 AuctionEventBus
`ConcurrentDictionary<Guid, ImmutableList<Channel<AuctionEvent>>>`. `Subscribe(id)` returns a bounded channel (capacity 1000, `FullMode = DropOldest` plus a `lagged` flag; a lagged subscriber is sent a snapshot and resets).

### 2.3 SubscribeAuction algorithm (no gap, no repeat)
```
ch = bus.Subscribe(id)                       // 1. register first, so nothing is missed
cur = HGET state seq                         // 2. current position
if last_seen == 0 or last_seen < ZRANGE events first score - 1 or state missing:
    snap = BuildSnapshot(id); write snap; sent = snap.seq
else:
    for e in ZRANGEBYSCORE events (last_seen, +inf): write e; sent = e.seq
loop:                                        // 3. live
    e = await ch.Read() or heartbeat timer (15 s)
    if e.seq <= sent: continue               // drop overlap with catch-up
    if e.seq > sent + 1: snap = BuildSnapshot(id); write snap; sent = snap.seq; continue   // safety
    write e; sent = e.seq
```
`BuildSnapshot` reads state hash + last 50 bids; same masking as spec 02; for non-Live auctions without a state hash, build from SQL.

### 2.4 Handles
Lua writes user ids. `UserHandleCache` (Api, memory + Redis `h:{userId}` → username, 1 h) converts to masked handles when mapping. Viewer identity is not known per subscriber on the Web-shared stream, so stream rows are **always masked**; the Web replaces the viewer's own rows with "you" by comparing `bidder.user_id` (sent alongside, user ids are not secret) with the signed-in user.

### 2.5 GetAuctionSnapshot
Unary version of `BuildSnapshot`.

## 3. Web side

### 3.1 LiveAuctionHub (singleton per Web copy)
```csharp
public sealed class LiveAuctionHub {
  public IDisposable Watch(Guid auctionId, Action<AuctionEvent> onEvent, out AuctionSnapshot? latest);
  // first watcher: start Feed(auctionId); later watchers get the latest snapshot copy immediately
}
sealed class Feed {   // one per auction
  long lastSeq; AuctionSnapshotModel model;          // kept current by applying events
  Task RunAsync(): loop { call SubscribeAuction(id, lastSeq); await foreach e → Apply(e); Broadcast(e) }
                   on RpcException: Status = Reconnecting; Broadcast(status); delay backoff; retry
  refCount; when 0 → start 30 s timer → cancel stream
}
```
`Apply(e)` updates the shared model (price, rows, end, status). Components read the model, so a late joiner has full state without another call.

### 3.2 Component pattern
`AuctionLive.razor` (wraps the detail page):
- `OnInitialized`: `hub.Watch(id, OnEvent, out snap)`.
- `OnEvent`: set `dirty = true`; a `PeriodicTimer(100 ms)` calls `InvokeAsync(StateHasChanged)` when dirty (≤ 10 renders/s). History rows are read from the model, so none are lost.
- `Dispose`: release the watch.
- Also used by browse cards (`AuctionCardLive`) for Live auctions on the current page.

### 3.3 Countdown and clock
- `wwwroot/js/clock.js`: `setOffset(ms)`, `startCountdown(el, endMs)`; updates text every 250 ms locally, turns red < 60 s, shows "Closing…" at 0.
- `ServerClock` (scoped per circuit): on each event/heartbeat `offset = 0.8*offset + 0.2*(server_ts − Date.now() from JS)`; JS interop pushes offset. Keeps server CPU low (no server-side ticking).

### 3.4 Reconnect UI
- Custom `components-reconnect-modal` markup: banner "Reconnecting… bidding paused".
- `Feed.Status` (`Live`, `Reconnecting`) shown as a `MudAlert`; bid button disabled while not `Live`.

## 4. Metrics
- `live_read_delay_ms` (Api), `live_fanout_delay_ms` (Web: now − event.server_ts at receive), `live_streams_open` (Web gauge), `live_subscribers` (Api gauge).
- R3.2 check: Playwright test with two browser contexts on web-1 and web-2; place bid in one; assert the other shows the price within 500 ms (`expect(...).toHaveText(..., {timeout: 500})`).
