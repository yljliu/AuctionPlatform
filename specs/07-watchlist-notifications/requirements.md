# 07 Watchlist and Notifications: Requirements

Covers: watch/unwatch, in-app notifications, live user feed (bell). Email is deferred. Design refs: v2 W5, 5.5.8 (`SubscribeUser`), 5.5.9.

## R1 Watchlist
1. WHEN a signed-in user calls `WatchAuction` THE SYSTEM SHALL add the auction to their watchlist (no error if already watched) and return the watcher count.
2. WHEN a signed-in user calls `UnwatchAuction` THE SYSTEM SHALL remove it (no error if not watched).
3. IF the auction is Settled or Cancelled THEN `WatchAuction` SHALL return `INVALID_STATE`.
4. THE SYSTEM SHALL keep a watcher count per Live auction in Redis and show it on the auction page.
5. WHEN a user calls `ListWatched` THE SYSTEM SHALL return Live and Scheduled watched auctions as cards.

## R2 Notifications created
THE SYSTEM SHALL create one notification per user per event (never two) for:

| Event | Who | Title example |
|---|---|---|
| Someone outbids you | the previous leader | "You've been outbid on Omega 1962" |
| Closing soon | watchers and bidders | "Omega 1962 ends in 1 minute" |
| You won | winner | "You won Omega 1962 for $1,250" |
| You lost | other bidders | "Omega 1962 sold for $1,250" |
| Your item sold / did not sell | seller | "Your item sold for $1,250" / "Your item did not sell" |
| Auction cancelled | bidders and watchers | "Omega 1962 was cancelled" |

1. THE SYSTEM SHALL NOT notify a user about their own action (e.g. the new leader is not told they were outbid).
2. Each notification SHALL link to the auction page (or the purchase/sale page for won/sold).

## R3 Delivery
1. WHILE the user has the site open THE SYSTEM SHALL push new notifications to the bell within 2 seconds and update the unread count.
2. WHEN the user opens the site later THE SYSTEM SHALL show notifications they missed.
3. WHEN a user is outbid while viewing that auction THE SYSTEM SHALL show the outbid snackbar from the auction feed, not a second popup from the bell.
4. `ListNotifications`, `MarkRead`, `MarkAllRead`, `GetUnreadCount` SHALL work as listed in v2 5.5.9.

## R4 Reliability
1. IF the notifier restarts THEN THE SYSTEM SHALL resume from where it stopped without missing or repeating notifications.
