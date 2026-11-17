# 07 Watchlist and Notifications: Tasks

- [ ] 1. WatchService
  - `watch.proto`; Watch/Unwatch idempotent with Redis watcher count; ListWatched; `INVALID_STATE` for ended auctions; `load.lua` sets `watchers`.
  - Tests: double watch → count 1; unwatch twice → count 0.
  - _Requirements: R1.1–R1.5_

- [ ] 2. Outbox relay
  - Relay worker per design §1; dedup-safe.
  - Test: rows published once; crash before UPDATE → resent, consumer dedups.
  - _Requirements: R4.1_

- [ ] 3. Notifier
  - Consumer groups on partition stream and `outbox`; recipient resolution with cache; event keys; actor excluded; batch insert; Pub/Sub hint.
  - Tests: outbid notifies only previous leader; ClosingSoon once despite extensions; settle → won/lost/sold rows; restart mid-batch → no duplicates.
  - _Requirements: R2, R2.1, R2.2, R4.1_

- [ ] 4. NotificationService RPCs
  - List (paged, unread filter), MarkRead, MarkAllRead, GetUnreadCount.
  - _Requirements: R3.2, R3.4_

- [ ] 5. SubscribeUser and Web bell
  - Api Pub/Sub fan-out stream; Web `UserFeed`, bell badge, popover, suppression while viewing an auction.
  - Check by hand: bob outbids alice on another page → alice's bell updates within 2 s; on the auction page alice sees only the snackbar.
  - _Requirements: R3.1, R3.3_
