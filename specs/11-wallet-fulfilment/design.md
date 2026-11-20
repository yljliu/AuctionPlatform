# 11 Wallet, Checkout and Fulfilment: Design

## 1. Money states

```
PendingPayment --pay (card or wallet)--> PaidInFull [escrow holds the money]
   --MarkShipped / MarkPickedUp--> (Shipment.ShippedAt)
   --ConfirmDelivery or 14 days--> (Shipment.DeliveredAt; return window opens if any)
   --window closes, no dispute--> Released [seller wallet + platform fees]
   --dispute (spec 12)--> Disputed --> Released / Refunded
```

`SettlementStatus` already has `PaidInFull`, `Released`, `Disputed` and `Refunded`. Shipping stage lives in `pay.Shipments`, not in the status.

## 2. Data (migration `AddWalletAndFulfilment`)

`pay.LedgerEntries`:

| Column | Type | Notes |
|---|---|---|
| `EntryId` | bigint identity PK | |
| `MovementId` | uniqueidentifier | the entries of one movement share it |
| `Account` | tinyint | 0 Wallet, 1 Escrow, 2 PlatformFees, 3 External |
| `UserId` | uniqueidentifier null | Wallet only |
| `SettlementId` | uniqueidentifier null | Escrow only |
| `Amount` | decimal(19,4) | + in, - out |
| `Currency` | char(3) | |
| `Kind` | nvarchar(30) | Deposit, Withdraw, PayByCard, PayByWallet, Payout, Fee, Refund |
| `CreatedAt` | datetime2 | |

Index (`Account`, `UserId`) include `Amount`; index (`SettlementId`).

`pay.Movements`: `MovementId` PK, `IdempotencyKey` unique, `Kind`, `UserId`, `CreatedAt` (one row per movement; the key makes repeats no-ops).

`pay.Payouts`: `PayoutId`, `UserId`, `Amount`, `MovementId`, `CreatedAt` ("Paid to your bank (demo)").

`ident.Addresses`: `AddressId`, `UserId`, `Name`, `Line1`, `Line2`, `City`, `Region`, `PostalCode`, `Country` (char 2), `IsDefault`, `CreatedAt`. At most 5 per user (checked in code).

`pay.Shipments`: `SettlementId` PK, `Carrier`, `Tracking`, `PickedUp` bit, `ShippedAt`, `DeliveredAt`, `DeliveredBy` (Buyer / Auto), `ReturnWindowEndsAt` null.

`pay.Settlements.ShippingAddressId` and `ShippingSnapshot` already exist.

## 3. Ledger (`Auction.Infrastructure/Payments/Ledger`)

```
MoveAsync(key, kind, user, legs[(account, userId?, settlementId?, amount)])
  require legs sum to 0                               (else throws: a bug)
  insert Movement (key)  -> duplicate key: return the existing MovementId (no new entries)
  insert entries
  (caller's transaction; SERIALIZABLE for wallet debits, see below)
BalanceAsync(account, id) = SUM(Amount)
```

Wallet debits (withdraw, pay by wallet) run in a transaction that first takes `sp_getapplock 'wallet:{userId}'` (exclusive, transaction owner), then checks the balance, then moves. Two debits at once wait on the lock, so the balance can't go below zero (R2.4).

| Movement | Legs |
|---|---|
| Deposit | External −a, Wallet(user) +a |
| Withdraw | Wallet(user) −a, External +a; plus a Payout row |
| PayByCard | External −t, Escrow(s) +t |
| PayByWallet | Wallet(buyer) −t, Escrow(s) +t |
| Payout | Escrow(s) −t, Wallet(seller) +(t − fee), PlatformFees +fee |

Spec 10 `PaymentManager` calls `Ledger.MoveAsync` inside its transaction (card: PayByCard; wallet: PayByWallet). `PayDemoRequest` gains `oneof method { DemoCard card; bool wallet; }`.

## 4. Contracts

`wallet.proto` (new): `GetWallet`, `Deposit(amount, DemoCard, idempotency_key)`, `Withdraw(amount, idempotency_key)`.

`account.proto` additions (in `auth.proto` service or a new `AccountService`): `ListAddresses`, `SaveAddress`, `DeleteAddress`, `SetDefaultAddress`.

`payment.proto` additions: `SetShippingAddress(settlement_id, address_id)`, `MarkShipped(settlement_id, carrier, tracking)`, `MarkPickedUp(settlement_id)`, `ConfirmDelivery(settlement_id)`. `SettlementRow` gains `stage`, `carrier`, `tracking`, `return_window_ends_ms`.

## 5. Lifecycle jobs (leader only, every 60 s, fenced)

```
AutoDeliver:  Shipments WHERE DeliveredAt IS NULL AND ShippedAt < now - 14 d  -> DeliveredAt = now, DeliveredBy = Auto, window
Payout:       Settlements s JOIN Shipments WHERE s.Status = PaidInFull AND DeliveredAt IS NOT NULL
              AND (ReturnWindowEndsAt IS NULL OR ReturnWindowEndsAt < now) AND no open dispute
              -> Ledger.MoveAsync(key = "payout:" + s.Id, Payout); s.Status = Released; outbox PayoutSent
```

## 6. Notifications

| Type | Topic | User | Title |
|---|---|---|---|
| `Shipped = 15` | Shipped | buyer | "{title} has shipped ({carrier} {tracking})" |
| `Delivered = 16` | Delivered | both | "{title} was delivered" |
| `PaidOut = 17` | PayoutSent | seller | "You were paid {amount} for {title}" |

Spec 10's `BuyerPaid` title becomes "Ship {title} to {buyer}".

## 7. Web

| Page | Change |
|---|---|
| `Me/Wallet.razor` (new) | Balance, Deposit (amount + card form from spec 10), Withdraw, movements table. |
| `Me/Addresses.razor` (new) | List, add, edit, delete, default. |
| `Me/Pay.razor` | Step 1 address (pick or add; skipped for local pickup), step 2 pay: card or "Pay with wallet (balance $x)". |
| `Me/Purchases.razor` | Stage column; "Confirm delivery" while Shipped. |
| `Seller/Sales.razor` | Stage column; "Mark shipped" dialog (carrier, tracking) or "Picked up". |
| AppBar menu | Wallet link with the balance. |
