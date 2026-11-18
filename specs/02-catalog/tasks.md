# 02 Catalog: Tasks

- [x] 1. Protos for catalog and seller
  - `catalog.proto`, `seller.proto` with messages from design Â§1. Build generates C#.
  - _Requirements: R1â€“R8_

- [x] 2. Category tree
  - `CategoryRepository`, `GetCategoryTree`, `ListCategories`, Redis cache 10 min, cache clear hook for admin changes.
  - Test: tree nests and sorts; inactive hidden.
  - _Requirements: R1.1, R1.2_

- [x] 3. Item rules and CreateItem/UpdateItem
  - `ItemRules` (Domain) with unit tests for each field rule.
  - `SellerGrpcService.CreateItem`, `UpdateItem` (row_version, `EDIT_LOCKED` when `LockedAt` set), `ListMyItems`, `DeleteDraftItem`.
  - Integration tests: non-seller â†’ `ROLE_REQUIRED`; missing shipping cost â†’ field error; locked edit refused.
  - _Requirements: R2.1â€“R2.5, R8.1_

- [x] 4. Image upload and serving
  - `UploadImage` client-stream RPC, magic-byte check, 5 MB cap, ImageSharp thumbnail, `ItemImages` row.
  - Web endpoint `/img/{id}` and `/img/{id}/thumb`.
  - Tests: PNG OK; `.exe` renamed `.png` â†’ `UPLOAD_INVALID`; 6 MB â†’ `UPLOAD_INVALID`.
  - _Requirements: R3.1â€“R3.3_

- [x] 5. Auction rules and CreateAuction
  - `AuctionRules.ValidateCreate` with unit tests (start too soon, duration bounds, reserve < start, defaults applied).
  - `CreateAuction` transaction with outbox row; map unique-index violation to `INVALID_STATE`.
  - Integration tests: success sets item Published and outbox row exists; second open auction â†’ `INVALID_STATE`; no image â†’ `IMAGE_REQUIRED`.
  - _Requirements: R4.1â€“R4.5_

- [x] 6. UpdateAuction and CancelAuction
  - Scheduled edits with row_version; Live cancel only with 0 bids via `ILiveAuctionControl` (stub returning true until spec 05).
  - Tests: edit after bid â†’ `EDIT_LOCKED`; cancel with bids â†’ `INVALID_STATE`.
  - _Requirements: R5.1â€“R5.4_

- [x] 7. Browse and search queries
  - `AuctionQueries` (Dapper) per design Â§5 with safe sort switch and LIKE escaping.
  - `ListAuctions`, `SearchAuctions`, `ListMyAuctions`.
  - Tests: category includes children; price filter; `%` in query is literal; paging totals.
  - _Requirements: R6.1, R6.2, R8.1_

- [x] 8. Live overlay and watching flag
  - `LiveStateReader` pipeline; `prices_delayed` when Redis fails; `watching` from `bidding.Watchlist` for the caller's page of ids.
  - Test: with Redis stopped, list still returns and flags delayed.
  - _Requirements: R6.3â€“R6.5_

- [x] 9. GetAuction
  - Detail assembly per design Â§6, handle masking, `SellerAuction` for owner/admin, no reserve leak.
  - Test: serialized `AuctionDetail` JSON never contains the reserve value or any `max` field.
  - _Requirements: R7.1â€“R7.4_
