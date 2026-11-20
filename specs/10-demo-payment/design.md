# 10 Demo Payment: Design

## 1. Flow

```
Purchases (/me/purchases)  --Pay-->  Pay page (/me/purchases/{settlementId}/pay)
   --PaymentService.PayDemo(settlement_id, card, idempotency_key)-->  Api (the fake payment API)
        FakeCardCheck.Check(card)            -> VALIDATION_FAILED with field issues, or ok
        PaymentManager.PayDemoAsync          (one SQL transaction)
   <--PaymentReceipt--  or  <--RpcException VALIDATION_FAILED (fields: card_number, expiry, cvc, name)--
Receipt page (/me/purchases/{settlementId}/receipt)
Outbox "PaymentReceived" --> relay --> notifier --> bell (buyer and seller)
```

## 2. Data

New table `pay.Payments` (migration `AddDemoPayments`):

| Column | Type | Notes |
|---|---|---|
| `PaymentId` | uniqueidentifier PK | also the receipt reference (`PAY-` + first 8 hex) |
| `SettlementId` | uniqueidentifier FK → `pay.Settlements` | |
| `BuyerId` | uniqueidentifier | |
| `Amount` | decimal(19,4) | equals `TotalDue - AmountPaid` at pay time |
| `Currency` | char(3) | |
| `Method` | nvarchar(20) | `DemoCard` |
| `CardLast4` | char(4) | nothing else about the card |
| `IdempotencyKey` | uniqueidentifier | unique index (`BuyerId`, `IdempotencyKey`) |
| `CreatedAt` | datetime2 | |

`pay.Settlements` is unchanged: `AmountPaid`, `Status` and `RowVersion` already exist.

## 3. Contract (`payment.proto`, new)

```proto
service PaymentService {
  rpc GetPayable (GetPayableRequest) returns (PayableSettlement);   // pay page summary; buyer only
  rpc PayDemo (PayDemoRequest) returns (PaymentReceipt);
  rpc GetReceipt (GetReceiptRequest) returns (PaymentReceipt);       // buyer only
}

message DemoCard {
  string number = 1;              // 16 digits; spaces and dashes allowed
  string expiry = 2;              // MM/YY
  string cvc = 3;                 // 3 digits
  string name = 4;
}

message PayDemoRequest {
  string settlement_id = 1;
  DemoCard card = 2;
  string idempotency_key = 3;     // a Guid made by the page when it opens
}

message PaymentReceipt {
  string payment_id = 1;
  string reference = 2;           // PAY-1A2B3C4D
  string settlement_id = 3;
  AuctionCard auction = 4;
  Money amount = 5;
  string card_label = 6;          // "Demo card •••• 1234"
  int64 paid_at_ms = 7;
}
```

`PayableSettlement` reuses the fields of `SettlementRow` (spec 06). `SettlementRow` gains `int64 paid_at_ms = 13` and `string payment_id = 14`.

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

The Web page runs the same `FakeCardCheck` for instant hints, but the server's check decides.

## 5. PaymentManager.PayDemoAsync (`Auction.Infrastructure/Payments`)

```
issues = FakeCardCheck.Check(card, today)
if issues                                 -> VALIDATION_FAILED (fields = issues); count invalid
if key empty                              -> VALIDATION_FAILED
existing = Payments where BuyerId = me and IdempotencyKey = key
if existing: same settlement -> return its receipt; else -> IDEMPOTENCY_MISMATCH
s = Settlements.Find(id)                  -> NOT_FOUND
s.BuyerId != me                           -> NOT_OWNER
s.Status != PendingPayment                -> INVALID_STATE
now > s.PaymentDeadline                   -> DEADLINE_PASSED
BEGIN TRAN
  insert Payment (CardLast4 = Last4(number)); s.AmountPaid = s.TotalDue; s.Status = PaidInFull; s.UpdatedAt = now
  audit "payment.demo" (actor = buyer, settlement id, last4, amount)
  outbox PaymentReceived { settlementId, auctionId, buyerId, sellerId, amount, paymentId }
  SaveChanges   (RowVersion conflict -> INVALID_STATE; duplicate key index -> re-read and return as a retry)
COMMIT
```

The card fields are never put in a log message, and `DemoCard` is never logged with `{@...}` (the spec 09 masking policy drops `Number` and `Cvc` as a second guard: add them to its drop list).

## 6. Notifications

Outbox topic `PaymentReceived` → `NotificationPlanner.FromOutbox` adds two drafts:

| Type (new) | User | Title | Link | Event key |
|---|---|---|---|---|
| `PaymentReceived = 8` | buyer | "Payment received for {title}" | `/me/purchases/{sid}/receipt` | `paid:{sid}` |
| `BuyerPaid = 9` | seller | "{buyer handle} paid for {title}" | `/seller/sales` | `paid-seller:{sid}` |

The unique `(UserId, EventKey)` index keeps them to one each (spec 07).

## 7. Web

| Page | Change |
|---|---|
| `Me/Purchases.razor` | Pay button enabled for `PendingPayment` → `/me/purchases/{id}/pay`; "Receipt" link for `PaidInFull`; "Overdue" chip after the deadline. |
| `Me/Pay.razor` (new) | Summary, demo banner, card form, Pay button with a spinner. The idempotency key is made in `OnInitialized` and kept across retries. On `VALIDATION_FAILED` show each field message under its field and keep the input. On success go to the receipt. |
| `Me/Receipt.razor` (new) | Receipt from `GetReceipt`. |
| `Seller/Sales.razor` | No code change; status shows "Paid in full" from the new status. |
