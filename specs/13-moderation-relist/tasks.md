# 13 Moderation and Relist: Tasks

- [ ] 1. Data
  - Entities, `ItemStatus.Removed`, `Auctions.RelistedAsId`, migration `AddModeration`, `RefreshTokens` IP index.
  - _Requirements: R1, R5.2_

- [ ] 2. Reports and queue
  - `ReportListing`, `ReportUser`, `ListReports`, `GetReportTarget`, `CloseReports`; linked-accounts query; `ShillWatch`.
  - Tests (integration): report twice → `ALREADY_APPLIED`; 11th in a day → `RATE_LIMITED`; own listing → `VALIDATION_FAILED`; two users on one IP are linked, ignored IPs are not; closing one closes all on the target; 3 linked outbids → one system report.
  - _Requirements: R1, R2_

- [ ] 3. Warn and remove listing
  - `WarnUser`, `RemoveListing`; notifications 23, 24.
  - Tests (integration): warn → notification and audit; remove → auction Cancelled, item `Removed`, bidders notified, relist refused.
  - _Requirements: R3_

- [ ] 4. Void bid
  - `ProxyResolver.Replay` (Domain); `BidVoider`; `rebuild.lua` reuse; event `BidVoided`; notification 25.
  - Tests (unit): replay of recorded sequences equals the live engine's final state; removing the top bid gives the second max at the right increment. (integration): void the leader's bid on a Live auction → price and leader fall back, seq +1, viewers get a snapshot, auction Live again with the same time left; void on Sealed → settles with the new result; void twice → one change; Settled → `INVALID_STATE`; persister behind for 10 s → `SERVICE_UNAVAILABLE`, still frozen.
  - _Requirements: R4_

- [ ] 5. Relist
  - `RelistAsync`, `RelistAuction`; notification 22.
  - Tests (integration): Unsold → new Scheduled auction, old links to it; Sold → `INVALID_STATE`; Defaulted with an accepted offer → `INVALID_STATE`; relist twice → `INVALID_STATE`; watchers notified.
  - _Requirements: R5_

- [ ] 6. Web pages
  - Report dialogs, admin Reports tab, Void buttons, voided rows in bid history, Relist on Listings.
  - Tests (bUnit): report needs a reason and 10+ characters; Void asks for a reason; Relist only on allowed rows.
  - _Requirements: R1.1, R2, R4.1, R5.1_

- [ ] 7. Demo check
  - Run the simulator on one auction, then as admin void the leader's bid → all open pages show the lower price within 2 s. As alice, report the listing; as admin, Reports → Remove listing. As a seller, relist an Unsold auction.
  - _Requirements: all_
