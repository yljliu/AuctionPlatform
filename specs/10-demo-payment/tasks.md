# 10 Demo Payment: Tasks

- [x] 1. Data and card check
  - `pay.Payments`, `ident.UserStrikes`, `pay.SecondChanceOffers`, `ops.Appeals` entities, migration `AddDemoPayments` (unique `Payments.SettlementId`; unique (`BuyerId`, `IdempotencyKey`); unique `UserStrikes.SettlementId`; unique `SecondChanceOffers.DefaultedSettlementId`; one open appeal per user).
  - `FakeCardCheck.Check` and `Last4`.
  - Tests (unit): 16 digits passes, with spaces and dashes too; 15 and 17 digits fail; letters fail; expiry last month fails, this month passes, `13/30` fails; CVC `12` and `1234` fail; blank name fails; several bad fields give several issues.
  - _Requirements: R2.3, R2.6_
  - Status: done. `FakeCardCheckTests` (29) pass.

- [x] 2. Fake payment API
  - `payment.proto`; `PaymentManager.PayDemoAsync`, `GetPayableAsync`, `GetReceiptAsync`; `PaymentGrpcService`; invalid-card limiter; `payments_total{result}`; add `Number` and `Cvc` to the log masking policy.
  - Tests (integration): valid card → `PaidInFull`, `AmountPaid = TotalDue`, one payment row with last4 only, audit and outbox rows; 15-digit card → `VALIDATION_FAILED` with field `card_number`, nothing changed; 6th invalid card in 10 min → `RATE_LIMITED`; paid, then submit again with a new key and a bad card → same receipt, `repeat = true`, still one payment; same key twice → same receipt; same key other settlement → `IDEMPOTENCY_MISMATCH`; other user → `NOT_OWNER`; past deadline → `DEADLINE_PASSED`; two at once → one payment, both get the receipt.
  - _Requirements: R1, R2.4, R2.5, R2.7, R3, R8_
  - Status: done. `PaymentTests` (9) passed on my run.

- [x] 3. Default and strikes
  - `DefaultSweeper` in the lifecycle loop; `payments_defaulted_total`, `strikes_total`.
  - Tests (integration): past-deadline settlement → `Defaulted`, auction outcome `Defaulted`, one strike, buyer still `Active`, outbox has `activeStrikes = 1`; the sweeper run twice → still one strike; third default → buyer `Suspended` with no end date, refresh tokens revoked, sign-in → `ACCOUNT_SUSPENDED`; fourth default while suspended → a fourth strike, one suspension audit entry; a paid settlement is never touched; a stale leader's fence blocks the write.
  - _Requirements: R5.1–R5.5_
  - Status: done. `DefaultStrikeTests` (7) passed on my run.

- [x] 4. Second-chance offer
  - `SecondChanceManager` and its four RPCs; expiry in the sweeper; `second_chance_total{result}`.
  - Tests (integration): next bidder is the highest distinct other bidder at their highest bid; no other bidder → `INVALID_STATE`; suspended next bidder → `INVALID_STATE`; send twice → one offer; other seller → `NOT_OWNER`; after 7 days → `DEADLINE_PASSED`; accept → new `PendingPayment` settlement with `IsSecondChance`, auction `Sold`; accept twice → same settlement; decline → Declined; past 48 h → Expired by the sweeper, accept → `DEADLINE_PASSED`.
  - _Requirements: R6_
  - Status: done. `SecondChanceTests` (7) passed on my run.

- [x] 5. Appeals
  - `AppealManager`; `SubmitAppeal`, `GetAppealStatus`, `ListAppeals`, `ResolveAppeal`.
  - Tests (integration): suspended user with the right password → Open appeal; wrong password → `INVALID_CREDENTIALS`; active user → `INVALID_STATE`; second open appeal → `ALREADY_APPLIED`; accept → user `Active`, can sign in, strikes cleared, settlements still `Defaulted`, next default is strike 1; reject without a note → `VALIDATION_FAILED`; reject → still suspended, a new appeal is allowed.
  - _Requirements: R7_
  - Status: done. `AppealTests` (5) passed on my run.

- [x] 6. Notifications
  - Topics `PaymentReceived`, `PaymentDefaulted`, `SecondChanceOffered`, `SecondChanceAccepted`, `SecondChanceDeclined`, `SecondChanceExpired` in `NotificationPlanner`; types 8 to 14.
  - Tests: paid → buyer and seller; default with 1 strike → seller and "1 of 3"; default at 3 → "suspended"; offer → bidder; answer → seller; each outbox message twice → still one each.
  - _Requirements: R4.4, R5.6, R6.3–R6.5_
  - Status: done. `PaymentNotificationTests` (9) pass.

- [x] 7. Web pages
  - `Me/Pay.razor`, `Me/Receipt.razor`, `Me/Offer.razor`, `Account/Appeal.razor`; Purchases and Sales statuses; Sales "Send second-chance offer"; strikes in the account menu; login "Write an appeal" link; admin Appeals tab; `SettlementRow.paid_at_ms`, `payment_id`.
  - Tests (bUnit): Pay button only for `PendingPayment` before the deadline; pay page for a paid settlement goes to the receipt; `VALIDATION_FAILED` shows the message under each bad field and keeps the input; `RATE_LIMITED` disables Pay and shows the minutes; success goes to the receipt; second-chance button only when allowed; offer page Accept leads to Pay; Appeals tab Reject needs a note.
  - _Requirements: R2.1, R2.2, R2.4, R2.7, R3.5, R4.1, R4.2, R4.5, R5.7, R6.1, R7.1, R7.4_
  - Status: done. `PurchaseViewTests` (22) pass; pages built, waiting for your by-hand check.

- [ ] 8. Demo check
  - Pay: win as `alice` → Purchases → Pay → `1234 5678 9012 3456`, `12/30`, `123`, a name → receipt; bell "Payment received"; open the pay page again → goes to the receipt; as the seller → Sales "Paid in full", bell "alice paid".
  - Bad card: on another win type 15 digits 6 times → field message 5 times, then "Too many tries".
  - Strikes: as `bob`, win 3 auctions (alice bidding second) and do not pay; push each deadline into the past (`UPDATE pay.Settlements SET PaymentDeadline = DATEADD(minute,-1,SYSUTCDATETIME()) WHERE BuyerId = ...`); bell "1 of 3", "2 of 3"; after the third, bob is signed out and cannot sign in.
  - Second chance: as the seller, Sales → "Send second-chance offer" to alice → as alice, bell → Accept → Pay.
  - Appeal: as bob, "Write an appeal" → send; as `admin`, Appeals tab → Accept; bob can sign in, strikes 0.
  - _Requirements: R4.3, R5, R6, R7_
  - Status: waiting for your run.
