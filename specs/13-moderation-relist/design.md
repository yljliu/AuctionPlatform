# 13 Moderation and Relist: Design

## 1. Data (migration `AddModeration`)

`ops.Reports`: `ReportId` bigint PK, `ReporterId` null (null = system), `TargetType` (Listing, User), `TargetId` (auction id or user id), `Reason` tinyint, `Message`, `Status` (Open, ActionTaken, NoAction), `ClosedBy`, `CloseNote`, `CreatedAt`, `ClosedAt`. Filtered unique (`ReporterId`, `TargetType`, `TargetId`) WHERE `Status = 0`.

`ops.Warnings`: `WarningId`, `UserId`, `AdminId`, `Note`, `ReportId` null, `CreatedAt`.

`catalog.Auctions.RelistedAsId` uniqueidentifier null (new column). `ItemStatus` gains `Removed`.

## 2. Linked accounts

```sql
SELECT DISTINCT r2.UserId, r1.Ip
FROM ident.RefreshTokens r1
JOIN ident.RefreshTokens r2 ON r2.Ip = r1.Ip AND r2.UserId <> r1.UserId
WHERE r1.UserId = @user AND r1.CreatedAt > @since AND r2.CreatedAt > @since AND r1.Ip IS NOT NULL
```

Index (`Ip`, `CreatedAt`) include `UserId` on `RefreshTokens`. Loopback and the compose proxy's address are excluded (config list `Moderation:IgnoreIps`), so the demo does not link everyone.

Shill hint (R1.3): the notifier already sees `Bid` stream entries with an `outbid` field; `ShillWatch` counts, per auction, outbids where the new leader is linked to the seller; at 3 it inserts a system report (unique per auction by event key `shill:{auctionId}`).

## 3. Void bid (`Auction.Infrastructure/Moderation/BidVoider`)

```
Void(admin, auctionId, eseq, reason):
  1. auction status Live or Sealed (SQL and Redis)          else INVALID_STATE
     rows already voided for this eseq                      -> return the earlier result
  2. freeze.lua (spec 05) with reason "void-bid"            (Live: remembers time left)
  3. wait until state.persistedEseq >= state.eseq (max 10 s, else SERVICE_UNAVAILABLE, stays frozen)
  4. SQL tran: UPDATE Bids SET VoidedAt, VoidedBy, VoidReason WHERE AuctionId AND ESeq = eseq
  5. replay: placed = SELECT the PlaceBid intents (UserId, MaxAmount, ESeq) not voided ORDER BY ESeq
             result = ProxyResolver.Replay(startingPrice, increment table, reserve, placed)
  6. rebuild.lua: the spec 05 recovery rebuild with result (price, leader, leaderMax, bidCount),
     seq + 1, event BidVoided {eseq, oldPrice, newPrice, oldLeader, newLeader}, keeps Frozen
  7. SQL: Auctions current price, bid count; audit "bid.void"
  8. Live: resume.lua (spec 05 resume, time left as frozen). Sealed: unfreeze to Sealed; the drain and settle go on.
```

`ProxyResolver.Replay` is new and pure (Domain): it feeds the placed bids one by one into the existing proxy step the Lua script mirrors, so the replay gives the same answer the live engine would have. It is unit tested against recorded bid sequences.

The `BidVoided` event makes `LiveAuctionHub` (spec 04) fetch a snapshot, as on a seq gap.

## 4. Relist (`Auction.Infrastructure/Catalog/AuctionManager.RelistAsync`)

```
Relist(seller, oldAuctionId, schedule, prices):
  old.SellerId = seller; old.RelistedAsId null
  old ended: Outcome Unsold, or Defaulted with no Accepted offer, or Cancelled by seller with 0 bids
  item not Removed; no Scheduled/Live auction for the item; no PendingPayment/PaidInFull settlement
  spec 02 validation on the new schedule and prices
  tran: new Auction (Scheduled) for the item; old.RelistedAsId = new.Id; item Status Published;
        outbox AuctionRelisted {old, new}
```

The notifier sends `Relisted = 22` to the old auction's watchers.

## 5. Contracts

`moderation.proto` (new, user side): `ReportListing`, `ReportUser`. `admin.proto`: `ListReports`, `GetReportTarget` (reports, history, linked accounts, strikes), `CloseReports`, `WarnUser`, `RemoveListing`, `VoidBid(auction_id, eseq, reason)`. `seller.proto`: `RelistAuction`.

## 6. Notifications

| Type | User | Title |
|---|---|---|
| `Warning = 23` | user | "Warning from the moderators: {note}" |
| `ListingRemoved = 24` | seller | "Your listing {title} was removed: {note}" |
| `BidVoided = 25` | voided bidder, new leader | "Your bid on {title} was removed by a moderator" / "You are now the leader on {title}" |
| `Relisted = 22` | old watchers | "{title} is listed again" |

## 7. Web

| Page | Change |
|---|---|
| Auction page | "Report listing" in a menu; seller box "Report seller". Bid history shows voided rows struck through ("removed by a moderator"). |
| Admin page | Reports tab (queue, target panel with history, linked accounts, actions). Auction admin panel: "Void" on each bid history row. |
| `Seller/Listings.razor` | "Relist" on ended rows that allow it; opens the spec 02 schedule and price form, filled from the old one. |
