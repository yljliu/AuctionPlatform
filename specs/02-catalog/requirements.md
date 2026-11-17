# 02 Catalog: Requirements

Covers: categories, items, auction creation and editing, browse, search, auction detail. Design refs: v2 W2, W4, W14; sections 4.2 (`catalog`), 5.5.5, 5.5.6.

## R1 Categories
1. WHEN anyone calls `GetCategoryTree` THE SYSTEM SHALL return active categories nested by `ParentCategoryId`, sorted by `SortOrder` then `Name`.
2. THE SYSTEM SHALL cache the tree in Redis for 10 minutes and clear it when an admin changes a category.

## R2 Create item
**User story:** As a seller, I want to describe my item once, so that I can auction it (and relist it later).
1. WHEN a user with role Seller and status Active calls `CreateItem` with valid fields THE SYSTEM SHALL save it as `Draft` and return the `Item`.
2. IF the caller lacks role Seller THEN THE SYSTEM SHALL return `ROLE_REQUIRED`.
3. IF `ShippingType` is BuyerPays and `ShippingCost` is missing, or `ReturnsAccepted` is true and `ReturnWindowDays` is missing, THEN THE SYSTEM SHALL return `VALIDATION_FAILED` naming the field.
4. WHEN the seller calls `UpdateItem` before the item is locked THE SYSTEM SHALL save the changes, checking `row_version`.
5. IF the item has `LockedAt` set THEN THE SYSTEM SHALL return `EDIT_LOCKED` for changes to Title, Description, Condition, Category.

## R3 Item image (MVP: one or more, stored on disk)
1. WHEN a seller uploads a JPEG, PNG or WebP up to 5 MB THE SYSTEM SHALL store it under `/data/images/{itemId}/{imageId}.{ext}`, create a 400 px thumbnail, and mark it Ready.
2. IF the file type (checked by magic bytes) or size is not allowed THEN THE SYSTEM SHALL return `UPLOAD_INVALID`.
3. THE SYSTEM SHALL serve images through Web at `/img/{imageId}` and `/img/{imageId}/thumb`.

## R4 Create auction
1. WHEN a seller calls `CreateAuction` for their own Draft or Unsold item THE SYSTEM SHALL validate and save an auction with status `Scheduled`, and set the item to `Published`.
2. THE SYSTEM SHALL require: start ≥ now + 1 min; duration 5 min to 14 days (5 min minimum so demos are quick); starting price > 0; reserve ≥ starting price when given; at least one Ready image.
3. THE SYSTEM SHALL default `SnipeWindowSec` 60, `ExtensionSec` 60, `MaxTotalExtensionSec` 3600, `IncrementTableId` 1, `FeeScheduleId` 1, `Currency` USD.
4. IF the item already has an auction in Scheduled, Live, Sealed, Settling or Frozen THEN THE SYSTEM SHALL return `INVALID_STATE`.
5. THE SYSTEM SHALL write an `AuctionCreated` outbox row in the same transaction.

## R5 Edit and cancel auction
1. WHILE an auction is Scheduled THE SYSTEM SHALL allow the seller to change times and prices (checking `row_version`).
2. WHILE an auction is Live with zero bids THE SYSTEM SHALL allow the seller to cancel it with a reason.
3. IF the auction has any bid, or has ended, THEN THE SYSTEM SHALL return `INVALID_STATE` for seller cancel and `EDIT_LOCKED` for edits.
4. WHEN an auction is cancelled THE SYSTEM SHALL set its status Cancelled, set the item back to Draft, write an `AuctionCancelled` outbox row, and (if Live) tell the lifecycle to mark it cancelled in Redis.

## R6 Browse and search
1. WHEN anyone calls `ListAuctions` THE SYSTEM SHALL return `AuctionCard`s filtered by category (including children), status (default Live), price range, ending-within, condition and seller; sorted by ending soon (default), newest, price or most bids; paged 20 per page (max 100).
2. WHEN anyone calls `SearchAuctions` with 2 to 100 characters THE SYSTEM SHALL match the words against Title and Description (case-insensitive `LIKE`) plus the same filters.
3. WHILE an auction is Live THE SYSTEM SHALL show the price, bid count and end time from Redis, not SQL, so browse matches the auction page.
4. IF Redis is unreachable THEN THE SYSTEM SHALL return SQL values and set `prices_delayed = true`.
5. WHEN the caller is signed in THE SYSTEM SHALL set `watching` on each card.

## R7 Auction detail
1. WHEN anyone calls `GetAuction` THE SYSTEM SHALL return `AuctionDetail` with item, images, seller summary, live state, `min_next_bid`, `reserve_met` (null if no reserve) and the last 50 public bids.
2. THE SYSTEM SHALL NOT include `reserve_price` or any bidder's max in `AuctionDetail`.
3. WHEN the caller is the seller or an admin THE SYSTEM SHALL return `SellerAuction`, which adds `reserve_price`.
4. WHEN the caller is signed in and has bid THE SYSTEM SHALL include `my_status` (their max, leading or not).

## R8 Seller lists
1. WHEN a seller calls `ListMyItems` or `ListMyAuctions` THE SYSTEM SHALL return only their own records, filterable by status.
