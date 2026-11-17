# 05 Auction Lifecycle: Requirements

Covers: loading, opening, closing-soon, sealing, draining, settling, cancel/freeze/resume/force-close, leader election with fencing, Redis-loss recovery. Design refs: v2 sections 2.2, 2.4, 2.5, W3, W14, W15.

## R1 Single leader
1. THE SYSTEM SHALL run lifecycle work on exactly one `workers` copy at a time, chosen by a lease in `bidding.LifecycleLeases` (TTL 15 s, renewed every 5 s).
2. WHEN the leader stops renewing THE SYSTEM SHALL let another copy take the lease within 20 seconds with a higher fencing token.
3. THE SYSTEM SHALL reject any settlement write whose fencing token is not the current one.

## R2 Load and open
1. WHEN a Scheduled auction starts within 5 minutes THE SYSTEM SHALL create its Redis state with status Scheduled.
2. WHEN Redis time reaches the start time THE SYSTEM SHALL set the auction Live in Redis and SQL and emit `state_changed(Opened)`, within 1 second.
3. Opening SHALL be idempotent: running it twice SHALL NOT emit two events.

## R3 Closing soon
1. WHEN a Live auction has 60 seconds or less left THE SYSTEM SHALL emit one `ClosingSoon` event (only once per auction, even after extensions).

## R4 Seal
1. WHEN Redis time reaches the current end time of a Live auction THE SYSTEM SHALL set it Sealed and emit `state_changed(Sealed)` within 1 second.
2. IF the end time has moved later (anti-sniping) THEN THE SYSTEM SHALL not seal and SHALL re-check at the new end time.
3. WHILE Sealed THE SYSTEM SHALL reject new bids with `NOT_LIVE`.

## R5 Drain then settle
1. THE SYSTEM SHALL settle a Sealed auction only after the persister has committed every stream entry for it (`persistedEseq ≥ eseq`).
2. IF draining takes more than 30 seconds THEN THE SYSTEM SHALL log a warning and raise the `settlement_lag` metric, and keep waiting; it SHALL NOT reopen bidding.
3. WHEN settling THE SYSTEM SHALL, in one SQL transaction with the fencing check: decide the outcome, create a Settlement when Sold, set Auction `Settled` with `Outcome`, set Item `Sold` or `Unsold`, and add an `AuctionSettled` outbox row.
4. THE SYSTEM SHALL decide: no bids → Unsold; price below reserve → Unsold; otherwise Sold to the leader at the current price.
5. FOR a Sold auction THE SYSTEM SHALL compute hammer price, buyer premium (fee schedule %), shipping (item shipping cost or 0), tax 0, total due, seller fee, and a payment deadline 72 hours later.
6. WHEN SQL commits THE SYSTEM SHALL set Redis status Settled, emit `state_changed(Settled)` with outcome, final price and winner handle, and expire the auction's Redis keys after 10 minutes.
7. Settlement SHALL be idempotent per auction: a rerun after a crash SHALL NOT create a second Settlement.

## R6 Control actions
1. WHEN a seller cancels a Live auction with no bids THE SYSTEM SHALL cancel it in Redis atomically (refuse if a bid landed first).
2. WHEN an admin force-cancels THE SYSTEM SHALL cancel it whatever the bid count and emit `state_changed(Cancelled)`.
3. WHEN an admin force-closes THE SYSTEM SHALL set the end time to now so the normal seal path runs.
4. WHEN an admin freezes THE SYSTEM SHALL set Frozen (bids get `AUCTION_FROZEN`); WHEN resumed THE SYSTEM SHALL set Live and push the end time later by the time spent frozen.
5. Every control action SHALL write an `AuditLog` row with actor and reason.

## R7 Recovery
1. WHEN a leader starts THE SYSTEM SHALL rebuild its due-lists from SQL (Scheduled soon, Live, Sealed).
2. IF SQL says an auction is Live but its Redis state is missing THEN THE SYSTEM SHALL rebuild the state from SQL with status Frozen and raise an admin alert; an admin must resume it.
