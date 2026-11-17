# 07 Watchlist and Notifications: Design

## 1. Sources of events

| Notification | Source | Read by notifier through |
|---|---|---|
| Outbid | `Bid` entries (`outbid` field) on `{pN}:stream` | consumer group `notify` on the partition stream |
| Closing soon | `State` entries type ClosingSoon on `{pN}:stream` | same |
| Cancelled | `State` entries type Cancelled | same |
| Won / Lost / Sold / Unsold | `AuctionSettled` rows in `notify.Outbox` | Outbox relay → stream `outbox` → consumer group `notify` |

Outbox relay (`Auction.Workers/OutboxRelay`): every 200 ms `SELECT TOP 500 ... WHERE PublishedAt IS NULL ORDER BY OutboxId`, `XADD outbox * id <OutboxId> topic <Topic> payload <json>`, then `UPDATE PublishedAt`. A crash between XADD and UPDATE re-sends; consumers dedup by `OutboxId`.

## 2. Notifier (`Auction.Workers/Notifier`)

Consumer group `notify` on `{pN}:stream` and on `outbox`. For each entry:
```
recipients = Resolve(entry)          // SQL, cached per auction for 1 min while Live:
                                     //   watchers = bidding.Watchlist; bidders = DISTINCT UserId FROM bidding.Bids
for user in recipients minus actor:
   eventKey = "{type}:{auctionId}:{seq or outboxId}"      // e.g. outbid:<id>:<seq>, soon:<id>, won:<id>
   INSERT notify.Notifications (... ) — unique (UserId, EventKey) makes repeats a no-op (catch dup-key)
   PUBLISH u:{userId} <notification json>                 // live hint only
XACK
```
`ClosingSoon` uses `eventKey = soon:{auctionId}` so extensions never send a second one.
Batch inserts with a table-valued parameter or `SqlBulkCopy` into a staging table + `MERGE` when a closing spike creates many rows.

## 3. Live user feed

- Api `LiveGrpcService.SubscribeUser`: subscribes to Redis Pub/Sub channel `u:{userId}` (one shared `ISubscriber` per Api with in-process fan-out), streams `UserEvent.notification`. Heartbeat 15 s.
- Pub/Sub is fine here: a missed message only delays the bell; the row is already in SQL and appears on next load (R3.2).
- Web `UserFeed` (scoped per circuit, started by `MainLayout` when signed in): holds unread count (from `GetUnreadCount` at start), increments on each event, raises `Changed`. Bell shows a `MudBadge`; clicking opens a `MudPopover` with the latest 10.
- R3.3: `UserFeed` exposes `SuppressFor(auctionId)`; `AuctionLive` calls it while mounted, so outbid notifications for that auction update the count but show no popup.

## 4. Watchlist

- `WatchAuction`: `INSERT ... WHERE NOT EXISTS`; if the auction is Live, `HINCRBY {pN}:a:{id}:state watchers 1` only when the insert affected a row. `UnwatchAuction` mirrors with `-1`.
- `load.lua` (spec 05) sets `watchers` from SQL count when loading.
- Snapshot and `AuctionDetail` include `watchers`.
