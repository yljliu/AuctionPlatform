# 10 Demo Payment: Tasks

- [ ] 1. Data and card check
  - `pay.Payments` entity, migration `AddDemoPayments`, unique (`BuyerId`, `IdempotencyKey`).
  - `FakeCardCheck.Check` and `Last4`.
  - Tests (unit): 16 digits passes, with spaces and dashes too; 15 and 17 digits fail; letters fail; expiry last month fails, this month passes, `13/30` fails; CVC `12` and `1234` fail; blank name fails; several bad fields give several issues.
  - _Requirements: R2.3, R2.6_

- [ ] 2. Fake payment API
  - `payment.proto`; `PaymentManager.PayDemoAsync`, `GetPayableAsync`, `GetReceiptAsync`; `PaymentGrpcService`; `payments_total{result}`; add `Number` and `Cvc` to the log masking policy.
  - Tests (integration): valid card → `PaidInFull`, `AmountPaid = TotalDue`, one payment row with last4 only, audit and outbox rows; 15-digit card → `VALIDATION_FAILED` with field `card_number` and nothing changed; same key twice → one payment, same receipt; same key other settlement → `IDEMPOTENCY_MISMATCH`; other user → `NOT_OWNER`; paid again with a new key → `INVALID_STATE`; past deadline → `DEADLINE_PASSED`; two at once → one wins.
  - _Requirements: R1, R2.4, R2.5, R3, R5_

- [ ] 3. Notifications
  - `PaymentReceived` outbox topic in `NotificationPlanner`; types `PaymentReceived`, `BuyerPaid`.
  - Tests: one buyer and one seller notification; the outbox message twice → still one each.
  - _Requirements: R4.4_

- [ ] 4. Pay page and receipt
  - `Me/Pay.razor`, `Me/Receipt.razor`, Purchases button and Receipt link; `SettlementRow.paid_at_ms`, `payment_id`.
  - Tests (bUnit): Pay button enabled only for `PendingPayment`; a `VALIDATION_FAILED` reply shows the message under each bad field and keeps the input; success goes to the receipt.
  - _Requirements: R2.1, R2.2, R2.4, R4.1, R4.2, R4.5_

- [ ] 5. Demo check
  - Check by hand: win an auction as `alice`, Purchases → Pay → type `1234 5678 9012 3456`, `12/30`, `123`, a name → Pay → receipt; bell shows "Payment received"; log in as the seller → Sales shows "Paid in full" and the bell shows "alice paid"; on another win type 15 digits → "Card number must be 16 digits."
  - _Requirements: R4.3_
