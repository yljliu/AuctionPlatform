# 11 Wallet, Checkout and Fulfilment: Tasks

- [ ] 1. Ledger
  - Entities and migration `AddWalletAndFulfilment`; `Ledger.MoveAsync`, `BalanceAsync`; the zero-sum check.
  - Tests (unit): unbalanced legs throw. (integration): deposit → wallet +a, External −a; same key twice → one movement; all entries sum to 0.
  - _Requirements: R1, R6.2_

- [ ] 2. Wallet
  - `wallet.proto`, `WalletManager`, `WalletGrpcService`; app lock for debits; `wallet_movements_total`.
  - Tests (integration): deposit with a bad card → `VALIDATION_FAILED`, nothing added; withdraw more than the balance → `INSUFFICIENT_FUNDS`; 10 withdrawals of 10 at once from 50 → exactly 5 succeed, balance 0.
  - _Requirements: R2_

- [ ] 3. Addresses and checkout
  - `ident.Addresses`, address RPCs, `SetShippingAddress` with the snapshot; pay by wallet and the ledger legs in spec 10's `PaymentManager`.
  - Tests (integration): 6th address → `VALIDATION_FAILED`; snapshot unchanged after the address is edited; pay by wallet → wallet −t, escrow +t, `PaidInFull`; wallet too low → `INSUFFICIENT_FUNDS`; card pay → External −t, escrow +t; pay without an address for a shipped item → `VALIDATION_FAILED` (field `address`).
  - _Requirements: R3_

- [ ] 4. Shipping and delivery
  - `MarkShipped`, `MarkPickedUp`, `ConfirmDelivery`; `AutoDeliver` job; notifications 15 and 16.
  - Tests (integration): buyer marks shipped → `NOT_OWNER`; ship before payment → `INVALID_STATE`; ship twice → same result; confirm → delivered, window set from `ReturnWindowDays`; 14 days later with no confirm → auto delivered.
  - _Requirements: R4_

- [ ] 5. Payout
  - `Payout` job; notification 17; `payouts_total`, `escrow_held`.
  - Tests (integration): delivered with no returns → `Released`, seller wallet + (t − fee), fees + fee, escrow 0; with a window → nothing until it ends; open dispute → nothing; job twice or from two leaders → one payout.
  - _Requirements: R5.1–R5.3_

- [ ] 6. Web pages
  - Wallet, Addresses, Pay steps, stages on Purchases and Sales, Mark shipped dialog, Confirm delivery.
  - Tests (bUnit): wallet button only when the balance covers the total; local pickup skips the address step; Mark shipped needs a carrier and tracking; stage words.
  - _Requirements: R2.1, R3.1–R3.3, R5.4, R5.5_

- [ ] 7. Demo check
  - As alice: Wallet → deposit $500 → win → Pay → pick address → Pay with wallet → balance drops. As the seller: Sales → Mark shipped (UPS, 1Z999). As alice: Purchases → Confirm delivery. Push `ReturnWindowEndsAt` into the past in SQL → within a minute the seller's wallet shows the payout.
  - _Requirements: all_
