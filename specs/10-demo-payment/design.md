# 10 Demo Payment: Design

## 1. Flows

```
Pay
Purchases (/me/purchases) --Pay--> Pay page (/me/purchases/{sid}/pay)
   page: GetPayable -> already PaidInFull?  -> go to receipt (R3.5)
   --PaymentService.PayDemo(settlement_id, card, idempotency_key)--> Api (fake payment API)
        PaymentManager.PayDemoAsync (section 5)
   <--PaymentReceipt  |  VALIDATION_FAILED (field issues)  |  RATE_LIMITED (retry_after_ms)
Receipt page (/me/purchases/{sid}/receipt)
Outbox PaymentReceived --> relay --> notifier --> bell (buyer and seller)

Default and strikes
Workers lifecycle leader, every 60 s: DefaultSweeper (section 6): Defaulted, +1 strike, 3 active strikes -> Suspended
Outbox PaymentDefaulted --> notifier --> bell (seller and buyer)

Second chance
Sales page --SendSecondChance--> offer (48 h) --bell--> next bidder --Accept--> new settlement --> Pay as above

Appeal
Login (ACCOUNT_SUSPENDED) --"Write an appeal"--> /account/appeal (static page, no sign-in)
   --AuthService.SubmitAppeal(username, password, message)--> AppealManager
Admin page, Appeals tab --AdminService.ListAppeals / ResolveAppeal--> AppealManager
```

## 2. Data

Migration `AddDemoPayments`.

`pay.Payments`:

| Column | Type | Notes |
|---|---|---|
| `PaymentId` | uniqueidentifier PK | also the receipt reference (`PAY-` + first 8 hex) |
| `SettlementId` | uniqueidentifier FK → `pay.Settlements` | **unique**: one payment per settlement (R3.3, R3.4) |
| `BuyerId` | uniqueidentifier | |
| `Amount` | decimal(19,4) | equals `TotalDue` |
| `Currency` | char(3) | |
| `Method` | nvarchar(20) | `DemoCard` |
| `CardLast4` | char(4) | nothing else about the card |
| `IdempotencyKey` | uniqueidentifier | unique (`BuyerId`, `IdempotencyKey`) |
| `CreatedAt` | datetime2 | |

`ops.Appeals`:

| Column | Type | Notes |
|---|---|---|
| `AppealId` | bigint identity PK | |
| `UserId` | uniqueidentifier FK | filtered unique index on `UserId` WHERE `Status = 0` (one open appeal) |
| `Message` | nvarchar(2000) | |
| `Status` | tinyint | 0 Open, 1 Accepted, 2 Rejected |
| `AdminId` | uniqueidentifier null | |
| `AdminNote` | nvarchar(400) null | required on reject |
| `CreatedAt`, `ResolvedAt` | datetime2 | |

`ident.UserStrikes`:

| Column | Type | Notes |
|---|---|---|
| `StrikeId` | bigint identity PK | |
| `UserId` | uniqueidentifier FK | index (`UserId`, `ClearedAt`) |
| `SettlementId` | uniqueidentifier | **unique**: one strike per defaulted settlement |
| `Reason` | nvarchar(100) | `NonPayment` |
| `CreatedAt` | datetime2 | |
| `ClearedAt` | datetime2 null | set when an appeal is accepted; active = `ClearedAt IS NULL` |

`pay.SecondChanceOffers`:

| Column | Type | Notes |
|---|---|---|
| `OfferId` | uniqueidentifier PK | |
| `DefaultedSettlementId` | uniqueidentifier | **unique**: one offer per default |
| `AuctionId`, `SellerId`, `BidderId` | uniqueidentifier | |
| `Amount` | decimal(19,4) | the bidder's highest bid |
| `Status` | tinyint | 0 Open, 1 Accepted, 2 Declined, 3 Expired |
| `ExpiresAt`, `CreatedAt`, `RespondedAt` | datetime2 | |
| `NewSettlementId` | uniqueidentifier null | set on accept |

`pay.Settlements`, `catalog.Auctions.Outcome` and `ident.Users.SuspendedUntil` already exist; `SuspendedUntil = null` with `Status = Suspended` already means "no end date" in `AuthManager`.

## 3. Contracts

`payment.proto` (new):

```proto
service PaymentService {
  rpc GetPayable (GetPayableRequest) returns (PayableSettlement);   // buyer only; includes status and payment_id
  rpc PayDemo (PayDemoRequest) returns (PaymentReceipt);
  rpc GetReceipt (GetReceiptRequest) returns (PaymentReceipt);       // buyer only
}

message DemoCard { string number = 1; string expiry = 2; string cvc = 3; string name = 4; }
message PayDemoRequest { string settlement_id = 1; DemoCard card = 2; string idempotency_key = 3; }
message PaymentReceipt {
  string payment_id = 1; string reference = 2; string settlement_id = 3; AuctionCard auction = 4;
  Money amount = 5; string card_label = 6; int64 paid_at_ms = 7;
  bool repeat = 8;                // true when this call returned an earlier payment (R3.2, R3.3)
}
```

`SettlementRow` gains `int64 paid_at_ms = 13` and `string payment_id = 14`.

`auth.proto` adds `rpc SubmitAppeal (SubmitAppealRequest) returns (AppealStatus)` and `rpc GetAppealStatus (AppealLoginRequest) returns (AppealStatus)`; both are anonymous and use the login rate limit.

`admin.proto` adds `rpc ListAppeals (ListAppealsRequest) returns (ListAppealsResponse)` and `rpc ResolveAppeal (ResolveAppealRequest) returns (google.protobuf.Empty)` (`accept` bool, `note`).

## 4. FakeCardCheck (`Auction.Domain/Payments`, pure)

```
Check(number, expiry, cvc, name, today) -> list of FieldIssue (empty = valid)
  digits = number without spaces and dashes
  digits is not 16 characters 0-9            -> ("card_number", "Card number must be 16 digits.")
  expiry not MM/YY, month not 01-12,
    or (20YY, MM) before today's year/month  -> ("expiry", "Expiry must be a future MM/YY.")
  cvc is not 3 characters 0-9                -> ("cvc", "CVC must be 3 digits.")
  name blank or longer than 100              -> ("name", "Enter the name on the card.")
Last4(number) -> last 4 of digits
```

The Web page runs the same check for instant hints; the server's check decides.

## 5. PaymentManager.PayDemoAsync (`Auction.Infrastructure/Payments`)

```
s = Settlements.Find(id)                  -> NOT_FOUND
s.BuyerId != me                           -> NOT_OWNER
if s.Status == PaidInFull                 -> return receipt of its payment, repeat = true    (R3.3; no card check)
byKey = Payments where BuyerId = me and IdempotencyKey = key
if byKey: same settlement -> receipt, repeat = true; else -> IDEMPOTENCY_MISMATCH
if s.Status != PendingPayment             -> INVALID_STATE
now > s.PaymentDeadline                   -> DEADLINE_PASSED
if limiter "payfail:{me}" has 5 in 10 min -> RATE_LIMITED (retry_after_ms)
issues = FakeCardCheck.Check(card, today)
if issues                                 -> limiter hit "payfail:{me}"; VALIDATION_FAILED (fields = issues)
BEGIN TRAN
  insert Payment (CardLast4 = Last4(number)); s.AmountPaid = s.TotalDue; s.Status = PaidInFull
  audit "payment.demo"; outbox PaymentReceived { settlementId, auctionId, buyerId, sellerId, amount, paymentId }
  SaveChanges
COMMIT
on unique-index violation (SettlementId or key) or RowVersion conflict:
  re-read; if s is PaidInFull -> return its receipt, repeat = true  (R3.4)   else rethrow
```

Only invalid cards count toward the limit; a successful payment does not. The limiter is the existing Redis `IRateLimiter` (spec 01).

## 6. DefaultSweeper (`Auction.Infrastructure/Lifecycle`, leader only)

Runs from `LifecycleLoop` every 60 s with the leader's fencing grant.

```
rows = SELECT TOP 100 Settlements WHERE Status = PendingPayment AND PaymentDeadline < now ORDER BY PaymentDeadline
for each s:
  BEGIN TRAN (fenced as other lifecycle SQL writes)
    s.Status = Defaulted                                       (only if still PendingPayment)
    auction.Outcome = Defaulted
    insert UserStrike (buyer, s)                               (unique SettlementId: a rerun adds nothing)
    active = COUNT strikes WHERE UserId = buyer AND ClearedAt IS NULL
    if active >= 3 and buyer.Status != Suspended:
       buyer.Status = Suspended; buyer.SuspendedUntil = null; revoke refresh tokens
       audit "user.suspend.strikes"
    audit "settlement.defaulted"
    outbox PaymentDefaulted { settlementId, auctionId, buyerId, sellerId, title, activeStrikes, suspended, canOfferSecondChance }
  COMMIT
  if suspended now: UserStatusCache.Invalidate(buyer)          (pub/sub, as UserAdminManager)
  payments_defaulted_total += 1; strikes_total += 1

offers = SELECT SecondChanceOffers WHERE Status = Open AND ExpiresAt < now  -> Status = Expired; outbox SecondChanceExpired
```

`canOfferSecondChance` = `SecondChance.NextBidder(auction)` is not null (below).

## 6a. SecondChanceManager (`Auction.Infrastructure/Payments`)

```
NextBidder(auction, defaulted buyer):
  SELECT TOP 1 UserId, MAX(Amount) FROM bidding.Bids
  WHERE AuctionId = a AND UserId <> buyer AND VoidedAt IS NULL
  GROUP BY UserId ORDER BY MAX(Amount) DESC, MIN(Seq)
  -> null when none, or when that user is Suspended/Banned
Send(seller, defaultedSettlementId):
  s Defaulted and s.SellerId = seller         -> else NOT_OWNER / INVALID_STATE
  now <= s.UpdatedAt + 7 days                 -> else DEADLINE_PASSED
  bidder = NextBidder                          -> null: INVALID_STATE "No other bidder can take it."
  insert offer (Open, ExpiresAt = now + 48 h)  -> unique DefaultedSettlementId: return the existing one
  outbox SecondChanceOffered
Accept(bidder, offerId) / Decline(bidder, offerId):
  offer.BidderId = me                          -> NOT_OWNER
  offer not Open: return it as it is (R6.6); Expired -> DEADLINE_PASSED
  accept: in one tran: new Settlement (IsSecondChance, HammerPrice = Amount, premium by fee schedule,
          shipping as the old one, deadline now + 72 h, PendingPayment); auction.Outcome = Sold;
          offer Accepted, NewSettlementId; outbox SecondChanceAccepted
  decline: offer Declined; outbox SecondChanceDeclined
```

The filtered unique index "one live obligation per auction" (spec 05) already excludes `Defaulted`, so the new settlement is allowed.

`payment.proto` adds `SendSecondChance`, `GetSecondChance`, `AcceptSecondChance`, `DeclineSecondChance`.
## 7. AppealManager (`Auction.Infrastructure/Identity`)

```
Submit(username, password, message, ip):
  login limiter (spec 01) -> RATE_LIMITED
  user by username, BCrypt verify  -> INVALID_CREDENTIALS (counts as a failed login)
  user.Status != Suspended         -> INVALID_STATE "Your account is not suspended."
  message 20..2000                 -> VALIDATION_FAILED
  insert Appeal (Open)             -> unique open index violation -> ALREADY_APPLIED
Resolve(adminId, appealId, accept, note):
  appeal Open                      -> else INVALID_STATE
  !accept and note blank           -> VALIDATION_FAILED
  accept: UserAdminManager.ReinstateAsync(adminId, user, "appeal accepted: " + note); UPDATE UserStrikes SET ClearedAt = now WHERE UserId = user AND ClearedAt IS NULL
  appeal.Status, AdminId, AdminNote, ResolvedAt; audit "appeal.accept" / "appeal.reject"
List: open first, newest first; joins the latest "user.suspend*" audit entry and the user's active strikes with their settlements.
```

## 8. Notifications

New `NotificationType` values and `NotificationPlanner.FromOutbox` topics:

| Type | Topic | User | Title | Link | Event key |
|---|---|---|---|---|---|
| `PaymentReceived = 8` | PaymentReceived | buyer | "Payment received for {title}" | `/me/purchases/{sid}/receipt` | `paid:{sid}` |
| `BuyerPaid = 9` | PaymentReceived | seller | "{buyer handle} paid for {title}" | `/seller/sales` | `paid-seller:{sid}` |
| `BuyerDefaulted = 10` | PaymentDefaulted | seller | "The buyer did not pay for {title}" | `/seller/sales` | `default-seller:{sid}` |
| `Strike = 11` | PaymentDefaulted (not suspended) | buyer | "You got a strike for not paying for {title} ({n} of 3)" | `/me/purchases` | `default:{sid}` |
| `AccountSuspended = 12` | PaymentDefaulted (suspended) | buyer | "Your account is suspended: 3 unpaid wins" | `/account/appeal` | `default:{sid}` |
| `SecondChance = 13` | SecondChanceOffered | bidder | "Second chance: buy {title} for {amount}" | `/me/offers/{offerId}` | `sco:{offerId}` |
| `SecondChanceAnswer = 14` | SecondChanceAccepted / Declined / Expired | seller | "{bidder} accepted / declined your offer for {title}" or "Your offer for {title} expired" | `/seller/sales` | `sco-{status}:{offerId}` |

The unique `(UserId, EventKey)` index keeps each to one (spec 07).

## 9. Web

| Page | Change |
|---|---|
| `Me/Purchases.razor` | Pay enabled for `PendingPayment` before the deadline; "Receipt" for `PaidInFull`; "Not paid (deadline passed)" for `Defaulted`. |
| `Me/Pay.razor` (new) | Calls `GetPayable` first: `PaidInFull` → navigate to the receipt. Summary, demo banner, card form, Pay button with a spinner. Idempotency key made in `OnInitialized`, kept across retries. `VALIDATION_FAILED` → message under each field, input kept. `RATE_LIMITED` → message with minutes left, Pay disabled until then. Success (`repeat` or not) → receipt. |
| `Me/Receipt.razor` (new) | Receipt from `GetReceipt`. |
| `Seller/Sales.razor` | Status words: "Paid in full", "Buyer did not pay"; "Send second-chance offer" button when allowed, with the bidder and amount; offer status after. |
| `Me/Offer.razor` (new) | `/me/offers/{id}`: item, amount, time left, Accept / Decline; after accept, link to Pay. |
| `Layout/AppBar.razor` | "Strikes: n of 3" in the account menu when n > 0. |
| `Account/Login.razor` | On `ACCOUNT_SUSPENDED` show the message and a "Write an appeal" link. |
| `Account/Appeal.razor` (new, static like login) | Username, password, message; shows the latest appeal's status and note. |
| `Admin` page | New Appeals tab: list, Accept / Reject with a note. |
