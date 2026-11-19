# 07 Watchlist and Notifications: Tasks

- [ ] 1. WatchService
  - `watch.proto`; Watch/Unwatch idempotent with Redis watcher count; ListWatched; `INVALID_STATE` for ended auctions; `load.lua` sets `watchers`.
  - Tests: double watch → count 1; unwatch twice → count 0.
  - _Requirements: R1.1–R1.5_
  - Status: code done; integration tests written (`WatchNotifyTests`), waiting for your run.

- [ ] 2. Outbox relay
  - Relay worker per design §1; dedup-safe.
  - Test: rows published once; crash before UPDATE → resent, consumer dedups.
  - _Requirements: R4.1_
  - Status: code done; integration test written (`WatchNotifyTests`, relay crash case), waiting for your run.

- [ ] 3. Notifier
  - Consumer groups on partition stream and `outbox`; recipient resolution with cache; event keys; actor excluded; batch insert; Pub/Sub hint.
  - Tests: outbid notifies only previous leader; ClosingSoon once despite extensions; settle → won/lost/sold rows; restart mid-batch → no duplicates.
  - _Requirements: R2, R2.1, R2.2, R4.1_
  - Status: code and unit tests done (`NotificationPlannerTests`); integration tests written (`WatchNotifyTests`), waiting for your run.

- [ ] 4. NotificationService RPCs
  - List (paged, unread filter), MarkRead, MarkAllRead, GetUnreadCount.
  - _Requirements: R3.2, R3.4_
  - Status: code done; integration test written (`WatchNotifyTests`), waiting for your run.

- [ ] 5. SubscribeUser and Web bell
  - Api Pub/Sub fan-out stream; Web `UserFeed`, bell badge, popover, suppression while viewing an auction.
  - Check by hand: bob outbids alice on another page → alice's bell updates within 2 s; on the auction page alice sees only the snackbar.
  - _Requirements: R3.1, R3.3_
  - Status: code and unit tests done (`UserFeedTests`); the check by hand is yours.

## Build notes (differences from design.md)

- `SubscribeUser` is on the new `NotificationService` (notification.proto), not `LiveService`: it needs a signed-in caller, and the Web's live client sends no token. The Api fans out with `UserEventHub` (one Pub/Sub subscription per user channel per Api copy).
- The Redis watcher count moves through `watchers.lua`, and only when the SQL row was really added or removed. It changes whenever the auction is in Redis (Scheduled after load, or Live), not only Live, so a watch between load and open still counts. `load.lua` takes the SQL count as ARGV[12]; a rebuilt auction gets it with an HSET.
- `AuctionDetail.card.watching` is now set for the viewer, so the auction page shows Watch or Watching.
- The outbox relay locks the rows it sends (UPDLOCK, READPAST) for the whole send, so two workers copies split the rows instead of both sending them. A crash after XADD and before the UPDATE still sends them again; event keys drop repeats.
- The notifier group on a partition stream starts at the end (`$`), so old bids from before the notifier existed are not announced; the outbox group starts at the beginning. Each copy reads its own unacknowledged entries first after a restart.
- Recipients (watchers and bidders) are read fresh from SQL for each event that needs them (closing soon, cancelled, settled); only the title, seller and currency are cached for 1 minute.
- One INSERT per event with OPENJSON and NOT EXISTS (plus UX_Notif_Dedup) saves the whole batch; only new rows are published.
- New type `AuctionCancelled = 7` in `NotificationType`. Cancelled comes from both the stream (Redis) and the AuctionCancelled outbox row (SQL); both use the key `cancelled:{id}`, so a user gets one.
- Unsold also tells the bidders ("ended without a sale"), as Lost.
- The bell is a `MudMenu` drop-down (latest 10, Mark all read, See all); a new notification also shows a snackbar unless the open auction page already shows it (outbid on that auction, R3.3).
