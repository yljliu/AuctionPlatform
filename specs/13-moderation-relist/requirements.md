# 13 Moderation and Relist: Requirements

Covers: the rest of admin moderation (design W15: reports, queue, linked accounts, warn, remove listing, void bid) and relist (W14). Ban, suspend, freeze, resume, force-close and force-cancel already exist (specs 01, 05, 06).

## R1 Reports
1. A signed-in user SHALL be able to report a listing or a user with a reason (Prohibited item, Counterfeit, Misleading, Shill bidding, Harassment, Other) and a message (10 to 1000 characters).
2. A user SHALL have at most one open report per target (`ALREADY_APPLIED`), and at most 10 reports per day (`RATE_LIMITED`). Reporting yourself or your own listing SHALL return `VALIDATION_FAILED`.
3. THE system SHALL also open a report by itself (reporter "system") WHEN one user's bids are outbid by the same seller's linked account (R2.3) 3 or more times in one auction (shill-bid hint), at most once per auction.

## R2 Admin queue
1. THE admin page SHALL have a Reports tab: open reports grouped by target, most reports first, then oldest first.
2. For each target THE queue SHALL show: the reports, past admin actions on it (audit log), and for users their strikes (spec 10), suspensions, listings and recent bids.
3. Linked accounts SHALL be users that signed in from the same IP address in the last 30 days (from `RefreshTokens.Ip`), shown with the shared IPs.
4. An admin SHALL close a report as "Action taken" or "No action" with a note; closing one SHALL close all open reports on that target.

## R3 Actions
1. **Warn** a user: a notification "Warning from the moderators: {note}" and an audit entry. Warnings SHALL be listed with the user.
2. **Suspend with an end date** and **ban**: as today (specs 01, 06); from the queue, with the report linked in the audit entry.
3. **Remove listing**: an admin force-cancels the auction (spec 05) and sets the item `Removed`, so the seller cannot relist it; bidders are notified as for a cancel; the seller is notified with the note.
4. **Void bid**: R4.

## R4 Void bid (W15)
1. An admin SHALL be able to void one accepted bid (one PlaceBid: all rows of that stream entry) on a Live or Sealed, not yet settled auction, with a reason.
2. THE SYSTEM SHALL freeze the auction, wait until all its bids are in SQL, mark the rows voided (`VoidedAt`, `VoidedBy`, `VoidReason`), recompute price, leader, leader max and bid count by replaying the remaining bids in order through the proxy rules (spec 03), and write the result to Redis as a new state with a higher `seq`.
3. THE SYSTEM SHALL emit a `BidVoided` event, so every viewer gets a new snapshot within 2 seconds (spec 04 catch-up).
4. A Live auction SHALL then resume with the time it had left when frozen (spec 05 resume rules); a Sealed one SHALL go on to settle with the new result.
5. THE bidder whose bid was voided and the new leader SHALL be notified. The audit log SHALL record the bid, the old and new price and leader.
6. Voiding a bid twice SHALL return the first result. Voiding on a Settled auction SHALL return `INVALID_STATE`.

## R5 Relist (W14)
1. WHEN an auction ended Unsold, or Defaulted with no accepted second-chance offer (spec 10), or was cancelled by the seller before any bid, THE seller SHALL be able to relist the item: a new auction for the same item with new start and end times and the same or new prices (spec 02 rules).
2. THE old auction SHALL stay as history and SHALL link to the new one ("Relisted as …").
3. An item that is `Removed`, `Sold` (paid or awaiting payment), or already in a Scheduled or Live auction SHALL NOT be relisted (`INVALID_STATE`).
4. Watchers of the old auction SHALL be notified "{title} is listed again".

## R6 Observability
1. THE SYSTEM SHALL count `reports_total{reason}`, `moderation_actions_total{action}` and `bids_voided_total`.
