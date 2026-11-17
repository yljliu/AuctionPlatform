# 02 Catalog: Design

## 1. Contracts

`catalog.proto` → `CatalogService` (public) and `seller.proto` → `SellerService` (role Seller). Messages follow v2 §5.5.1 (`Category`, `Image`, `Item`, `AuctionCard`, `AuctionDetail`, `SellerAuction`, `PublicBidRow`, `MyBidStatus`).

MVP paging: `int32 page` (1-based) + `int32 page_size`; response has `int32 total`. (Keyset later.)

`AuctionCard` adds `bool prices_delayed`. `ListAuctionsResponse` also has `prices_delayed` at the top level.

`AuctionDetail` adds `repeated IncrementTier increment_tiers` (`from`, `increment` as `Money`) so the Web can compute quick-bid amounts, and `int32 watchers`.

## 2. Classes

| Class | Project | Job |
|---|---|---|
| `CategoryService` | Api | gRPC; reads via `CategoryRepository`, cache `cache:cattree` 10 min |
| `CatalogGrpcService` | Api | ListAuctions, SearchAuctions, GetAuction |
| `SellerGrpcService` | Api | CreateItem, UpdateItem, image upload, CreateAuction, UpdateAuction, CancelAuction, ListMy* |
| `ItemRules` | Domain | Validation rules for items (pure) |
| `AuctionRules` | Domain | Validation for create/update/cancel, given current status and bid count |
| `AuctionQueries` | Infrastructure | Dapper SQL for browse and search |
| `LiveStateReader` | Infrastructure | Pipelined `HMGET` of `{a:ID}:state` for many auctions |
| `ImageStore` | Infrastructure | Writes files under `/data/images`, thumbnails with `SixLabors.ImageSharp` |
| `OutboxWriter` | Infrastructure | Adds `notify.Outbox` row inside the current EF transaction |

## 3. Image upload (MVP simplification)

Instead of signed URLs, Web posts the file to Api using a **client-streaming** RPC:

```proto
rpc UploadImage(stream UploadImageChunk) returns (Image);
message UploadImageChunk { oneof part { UploadImageHeader header = 1; bytes data = 2; } }
message UploadImageHeader { string item_id = 1; string content_type = 2; int64 bytes = 3; }
```
Chunks of 64 KB. Api checks magic bytes on the first chunk, enforces the size cap while streaming, writes to a temp file, then makes the thumbnail and moves both into place. `ItemImages.StorageKey` = relative path.

Web serves `/img/{id}` and `/img/{id}/thumb` by calling `GetImage(image_id, thumb)` (server stream of bytes) and caching in memory for 5 min. Simpler alternative if short on time: mount the `images` volume read-only into web and serve with `PhysicalFileProvider`.

## 4. Create auction flow

```
SellerGrpcService.CreateAuction
  → load item (must be own, status Draft or Unsold)
  → AuctionRules.ValidateCreate(req, now)        // throws DomainException(VALIDATION_FAILED, fields)
  → check ItemImages has ≥1 Ready                // IMAGE_REQUIRED
  → BEGIN TRAN
      insert Auction (Status=Scheduled, EndTime = ScheduledEndTime)
      update Item.Status = Published
      OutboxWriter.Add("AuctionCreated", auctionId)
    COMMIT                                       // unique filtered index catches double-open → INVALID_STATE
  → return SellerAuction
```

Lifecycle (spec 05) loads Scheduled auctions into Redis 5 minutes before start. Spec 02 never writes Redis auction state.

## 5. Browse query (Dapper)

```sql
SELECT a.AuctionId, a.ItemId, i.Title, img.ImageId AS ThumbId, i.CategoryId, i.Condition, a.Status,
       a.Currency, COALESCE(a.CurrentPrice, a.StartingPrice) AS Price, a.BidCount,
       a.StartTime, a.EndTime, a.ReservePrice, a.BuyNowPrice
FROM catalog.Auctions a
JOIN catalog.Items i ON i.ItemId = a.ItemId
OUTER APPLY (SELECT TOP 1 ImageId FROM catalog.ItemImages
             WHERE ItemId = i.ItemId AND Status = 1 ORDER BY SortOrder) img
WHERE a.Status = @Status
  AND (@CategoryIds IS NULL OR i.CategoryId IN @CategoryIds)   -- expanded from tree in C#
  AND (@MinPrice IS NULL OR COALESCE(a.CurrentPrice, a.StartingPrice) >= @MinPrice)
  AND (@MaxPrice IS NULL OR COALESCE(a.CurrentPrice, a.StartingPrice) <= @MaxPrice)
  AND (@EndingBefore IS NULL OR a.EndTime <= @EndingBefore)
  AND (@SellerId IS NULL OR a.SellerId = @SellerId)
  AND (@Conditions IS NULL OR i.Condition IN @Conditions)
  AND (@Q IS NULL OR i.Title LIKE @Q OR i.Description LIKE @Q)  -- @Q = '%' + escaped + '%'
ORDER BY <sort> OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY;
```
Escape `%`, `_`, `[` in user search text. Sort is a whitelisted switch, never string-built from input.

After the query, `LiveStateReader` overlays `price`, `bidCount`, `endTime`, `status` for Live auctions from Redis in one pipeline. `reserve_met` = `price >= reserve` (null when no reserve); the reserve value itself is dropped before mapping to the card.

## 6. GetAuction

1. SQL: auction + item + images + seller profile (one query with multi-mapping).
2. Redis: `HGETALL {a:ID}:state` and `ZREVRANGE {a:ID}:bids 0 49` (if Live/Sealed). Fall back to SQL `Bids` top 50 for Settled.
3. `min_next_bid` from `IncrementTable.MinNext(price, hasBids, startingPrice)` (Domain, spec 03).
4. Mask bidder handles: `Mask(username)` = first char + `***` + last 2 chars; unmasked if viewer is that bidder or admin.
5. `my_status`: from `{a:ID}:state` field `leader` and a Redis hash `{a:ID}:max` (spec 03) for the caller only.

## 7. Edit lock

Spec 03's persister sets `Items.LockedAt` and `Auctions.BidCount` on the first bid. `AuctionRules` uses `BidCount > 0` for edit and cancel checks. Read `BidCount` from Redis while Live for freshness.

## 8. Cancel while Live

Seller cancel calls `ILiveAuctionControl.CancelAsync(auctionId)` (implemented in spec 05): Lua sets Redis status Cancelled only if `bidCount == 0`, returns false otherwise (race-safe). Only after it returns true does SQL commit the Cancelled status.
