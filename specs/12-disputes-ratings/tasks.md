# 12 Disputes, Refunds and Ratings: Tasks

- [ ] 1. Data
  - Entities and migration `AddDisputesAndRatings`.
  - _Requirements: R1.2, R4.1_

- [ ] 2. Open and reply
  - `DisputeManager.Open`, `Reply`; `dispute.proto`; photos; notifications 18, 19; 3-day flag.
  - Tests (integration): open in the window → `Disputed`, payout job skips it; open after the window → `DEADLINE_PASSED`; "not received" before 7 days after shipping → `DEADLINE_PASSED`; second dispute → `ALREADY_APPLIED`; stranger replies → `NOT_OWNER`.
  - _Requirements: R1, R2.1, R2.4, R2.5_

- [ ] 3. Decisions and refunds
  - `Decide`, `SellerRefund`; ledger refund legs; notification 20; `disputes_*` metrics.
  - Tests (integration): full refund, wallet payer → wallet +t, escrow 0, `Refunded`; full refund, card payer → External +t; part refund 30 of 100 → buyer +30, seller +(70 − fee), `Released`; reject → `PaidInFull`, then payout; decide twice → money moves once; no note → `VALIDATION_FAILED`; ledger still sums to 0.
  - _Requirements: R2.2, R2.3, R3_

- [ ] 4. Ratings
  - `Rate`, `RatingAggregator`, `ListSellerRatings`, `HideRatingComment`; notification 21.
  - Tests (integration): rate before delivery → `INVALID_STATE`; twice → `ALREADY_RATED`; after 30 days → `DEADLINE_PASSED`; aggregator → `RatingSum`/`RatingCount` right; hidden comment still counts stars.
  - _Requirements: R4_

- [ ] 5. Web pages
  - Dispute thread page, Purchases/Sales buttons, seller profile page, admin Disputes tab, rating dialogs.
  - Tests (bUnit): Open dispute only when allowed; part refund amount must be between 0 and the total; stars required.
  - _Requirements: R1.1, R2, R4.5_

- [ ] 6. Demo check
  - After a spec 11 delivery: as alice, Open dispute "Damaged" with a photo; as the seller, reply; as admin, Disputes → part refund $20 → alice's wallet +20, the seller paid the rest. Then alice rates the seller 5 stars → the auction page seller box shows it within a minute.
  - _Requirements: all_
