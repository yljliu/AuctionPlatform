# 03 Bidding Engine: Requirements

Covers: `PlaceBid`, proxy bidding, increments, anti-sniping, idempotency, durability, persister, bid history. Design refs: v2 sections 2.3, 2.4, W1, 4.2 (`bidding`), 5.5.7.

## R1 Who may bid
1. WHEN a signed-in user with status Active calls `PlaceBid` THE SYSTEM SHALL evaluate the bid.
2. IF the user is Suspended or Banned THEN THE SYSTEM SHALL return `ACCOUNT_SUSPENDED` or `ACCOUNT_BANNED` without running the bid script.
3. IF the user is the auction's seller THEN THE SYSTEM SHALL return `OWN_LOT`.
4. IF the user exceeds 5 bids per second THEN THE SYSTEM SHALL return `RATE_LIMITED`.

## R2 Auction must be open
1. IF the auction status is not Live THEN THE SYSTEM SHALL return `NOT_LIVE` (or `AUCTION_FROZEN` when Frozen).
2. IF Redis server time is at or after the auction end time THEN THE SYSTEM SHALL return `ENDED`.
3. THE SYSTEM SHALL use only Redis `TIME` for these checks.

## R3 Minimum bid
1. WHILE an auction has no bids THE SYSTEM SHALL accept a max bid ≥ the starting price.
2. WHILE an auction has bids THE SYSTEM SHALL require a max bid ≥ current price + increment, where the increment comes from the auction's increment table for the current price.
3. IF the max bid is too low THEN THE SYSTEM SHALL return `BELOW_MINIMUM` with `min_next_bid`.
4. THE SYSTEM SHALL reject amounts ≤ 0, with more than 2 decimal places, or in another currency, with `VALIDATION_FAILED`.

## R4 Proxy bidding
**User story:** As a bidder, I want to enter the most I will pay, so that the system bids for me and I pay as little as possible.
1. WHEN the first bid arrives THE SYSTEM SHALL make that bidder the leader at the starting price.
2. WHEN a challenger's max is greater than the leader's max THE SYSTEM SHALL make the challenger the leader at min(challenger max, leader max + increment).
3. WHEN a challenger's max is less than or equal to the leader's max THE SYSTEM SHALL keep the leader and set the price to min(leader max, challenger max + increment); on a tie the earlier leader wins at that max.
4. WHEN the current leader raises their own max THE SYSTEM SHALL store the new max, not change the price, and return `MAX_RAISED`.
5. IF the leader's new max is not above their old max THEN THE SYSTEM SHALL return `BELOW_MINIMUM`.
6. THE SYSTEM SHALL never reveal any bidder's max to anyone except that bidder and admins.
7. THE SYSTEM SHALL produce exactly the same price and leader as the C# `ProxyResolver` for every case in `tests/vectors/proxy.json`.

## R5 Anti-sniping
1. WHEN a bid changes the price within `SnipeWindowSec` of the end THE SYSTEM SHALL extend the end time by `ExtensionSec`, capped so total extensions never exceed `MaxTotalExtensionSec`.
2. THE SYSTEM SHALL return `extended = true` and the new `end_time_ms` in the reply and the event.
3. A `MAX_RAISED` bid SHALL NOT extend the auction.

## R6 Idempotency
1. WHEN the same `idempotency_key` is sent again with the same user and amount THE SYSTEM SHALL return the first outcome (success or rejection) without re-evaluating, for 24 hours.
2. IF the same key arrives with a different user or amount THEN THE SYSTEM SHALL return `IDEMPOTENCY_MISMATCH`.

## R7 Atomic and ordered
1. THE SYSTEM SHALL check and update an auction in one Redis Lua script so that no two bids see the same old price.
2. THE SYSTEM SHALL give every public auction event a per-auction `seq` that rises by exactly 1.

## R8 Durability
1. WHEN a replica is configured THE SYSTEM SHALL confirm a bid only after `WAIT 1 50` reports one replica; otherwise it SHALL return `SERVICE_UNAVAILABLE` (retryable; a retry with the same key returns the stored result).
2. WHEN a bid is accepted THE SYSTEM SHALL append it to the partition stream in the same script.
3. THE SYSTEM SHALL write every accepted stream entry to SQL exactly once, in order per auction, and acknowledge it only after commit.
4. IF the persister crashes THEN THE SYSTEM SHALL process its unacknowledged entries on restart.
5. IF an entry fails to persist 10 times THEN THE SYSTEM SHALL move it to the dead-letter stream, freeze that auction, and log a critical error.
6. THE SYSTEM SHALL keep persist lag (age of the oldest unacknowledged entry) under 2 seconds at 500 bids/s.

## R9 First-bid side effects
1. WHEN the first bid on an auction is persisted THE SYSTEM SHALL set `Items.LockedAt` (edit lock).

## R10 Bid history and my status
1. WHEN anyone calls `GetBidHistory` THE SYSTEM SHALL return public rows newest first with masked handles.
2. WHEN a signed-in user calls `GetMyBidStatus` THE SYSTEM SHALL return their max, whether they lead, and `min_next_bid`.
3. WHEN a signed-in user calls `ListMyBids` THE SYSTEM SHALL return auctions they bid on with their max, latest amount, leading flag and outcome.
