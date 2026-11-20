# 12 Disputes, Refunds and Ratings: Design

## 1. Dispute states

```
PaidInFull --OpenDispute--> Disputed
  Dispute: Open --seller "Refund buyer"--> Refunded (full)
           Open --admin refund full------> Refunded
           Open --admin refund part------> PartRefunded   (settlement Released; rest paid out)
           Open --admin reject-----------> Rejected       (settlement PaidInFull; payout per spec 11)
```

## 2. Data (migration `AddDisputesAndRatings`)

`pay.Disputes`: `DisputeId` PK, `SettlementId` **unique**, `BuyerId`, `SellerId`, `Reason` (tinyint: NotReceived, NotAsDescribed, Damaged, Other), `Status` (Open, Refunded, PartRefunded, Rejected), `RefundAmount` null, `DecidedBy` null, `DecisionNote` null, `CreatedAt`, `SellerFirstReplyAt` null, `ClosedAt` null.

`pay.DisputeMessages`: `MessageId` bigint PK, `DisputeId`, `AuthorId`, `AuthorRole` (Buyer, Seller, Admin), `Text`, `CreatedAt`.

`pay.DisputePhotos`: `PhotoId`, `MessageId`, `Path`, `ThumbPath` (stored like item images, folder `disputes/{disputeId}`).

`catalog.Ratings`: `RatingId` PK, `SettlementId`, `RaterId`, `RateeId`, `Role` (BuyerRatesSeller, SellerRatesBuyer), `Stars` tinyint 1..5, `Comment` nvarchar(500), `CommentHidden` bit, `CreatedAt`. Unique (`SettlementId`, `RaterId`).

## 3. DisputeManager (`Auction.Infrastructure/Disputes`)

```
Open(buyer, sid, reason, text, photos):
  s.BuyerId = buyer; s.Status = PaidInFull; shipment rules per R1.1; no dispute yet (unique index)
  tran: insert Dispute + first message; s.Status = Disputed; outbox DisputeOpened
Reply(user, did, text, photos): user is buyer or seller (or admin); dispute Open; first seller reply sets SellerFirstReplyAt
SellerRefund(seller, did)  = Decide(full) with actor seller
Decide(admin, did, kind, amount?, note):
  dispute Open, note required, 0 < amount < total for part
  tran (key "dispute:{did}"):
    full:   Ledger Refund: Escrow(s) -t, (Wallet(buyer) or External) +t; s.Status = Refunded
    part:   Refund leg for amount; Payout legs for t - amount with fee on (t - amount); s.Status = Released
    reject: s.Status = PaidInFull
    dispute Status, DecidedBy, DecisionNote, ClosedAt; audit "dispute.decide"; outbox DisputeDecided
```

"Paid with wallet" = the settlement's escrow movement Kind is `PayByWallet` (spec 11 ledger).

Spec 11's payout job already skips settlements that are `Disputed`; a rejected dispute puts it back to `PaidInFull`, and the next run pays out if the window has closed.

## 4. Ratings

```
Rate(user, sid, stars, comment):
  user is buyer or seller of s; shipment DeliveredAt set; now <= DeliveredAt + 30 d
  insert Rating (unique) -> ALREADY_RATED; outbox Rated
RatingAggregator (workers, every 60 s): for sellers with ratings newer than their last run,
  UPDATE SellerProfiles SET RatingSum = SUM(Stars), RatingCount = COUNT(*) WHERE RateeId = seller AND Role = BuyerRatesSeller
```

## 5. Contracts

`dispute.proto` (new): `OpenDispute`, `GetDispute` (thread), `ReplyDispute`, `SellerRefund`, `ListMyDisputes`. `admin.proto`: `ListDisputes` (open first, flag "no seller reply 3 d"), `DecideDispute`, `HideRatingComment`. `catalog.proto`: `ListSellerRatings(handle, page)`, `RateSettlement`.

## 6. Notifications

| Type | Topic | User | Title |
|---|---|---|---|
| `DisputeOpened = 18` | DisputeOpened | seller | "{buyer} opened a dispute on {title}" |
| `DisputeMessage = 19` | DisputeMessage | the other side | "New message on the dispute for {title}" |
| `DisputeDecided = 20` | DisputeDecided | both | "Dispute on {title}: refunded / part refunded {amount} / rejected" |
| `Rated = 21` | Rated | ratee | "{name} rated you {n} stars" |

Admins see new disputes in the admin Disputes tab (no bell entry).

## 7. Web

| Page | Change |
|---|---|
| `Me/Purchases.razor` | "Open dispute" when allowed; "Rate seller" after delivery. |
| `Me/Dispute.razor` (new) | `/me/disputes/{id}`: thread, reply with photos, status. Used by buyer and seller. |
| `Seller/Sales.razor` | "Dispute open" chip linking to the thread; "Refund buyer"; "Rate buyer". |
| `Sellers/Profile.razor` (new) | `/sellers/{handle}`: average, count, ratings list. |
| Admin page | Disputes tab: queue, thread, Decide dialog (full, part with amount, reject; note). Ratings: hide comment. |
| Auction page seller box | Link to `/sellers/{handle}`. |
