# 10 Demo Payment: Requirements

Covers: paying for a won auction through a fake payment API, so a demo can go from "you won" to "paid"; what happens when a buyer does not pay (strikes, suspension at 3, second-chance offer: design W11, W12); and appeals. No real money moves. Replaces the disabled "Coming soon" Pay button from spec 06 R4.3. Wallet payments, shipping address, escrow and payout are spec 11; refunds and disputes are spec 12; relist is spec 13. Installments and real payment providers are out of scope.

## R1 Who can pay
1. THE SYSTEM SHALL let only the buyer of a settlement pay it. Anyone else SHALL get `NOT_OWNER` (unknown id: `NOT_FOUND`).
2. THE SYSTEM SHALL accept a payment only WHILE the settlement is `PendingPayment` and its `PaymentDeadline` has not passed.
3. IF the deadline has passed THEN THE SYSTEM SHALL return `DEADLINE_PASSED`.

## R2 The fake payment API checks the card
1. THE pay page SHALL show the item, hammer price, buyer premium, shipping, total due and pay-by time, and a banner "Demo payment: no real money is charged".
2. THE form SHALL ask for card number, expiry (MM/YY), CVC and name on card, and send them to the fake payment API.
3. THE fake payment API SHALL check the card details on the server:

| Field | Valid | Error message |
|---|---|---|
| Card number | exactly 16 digits (spaces and dashes are ignored) | "Card number must be 16 digits." |
| Expiry | MM/YY, month 01–12, this month or later | "Expiry must be a future MM/YY." |
| CVC | exactly 3 digits | "CVC must be 3 digits." |
| Name on card | not empty, at most 100 characters | "Enter the name on the card." |

4. IF any field is invalid THEN THE SYSTEM SHALL return `VALIDATION_FAILED` with one field issue per bad field, change nothing, and THE pay page SHALL show each message under its field and keep what the user typed.
5. IF all fields are valid THEN THE payment SHALL be approved.
6. THE SYSTEM SHALL store only the last 4 digits of the card. It SHALL NOT store or log the full number, the expiry or the CVC.
7. THE SYSTEM SHALL allow at most 5 invalid card attempts per user in 10 minutes. The 6th SHALL return `RATE_LIMITED` with `retry_after_ms`, before the card is checked, and THE pay page SHALL show "Too many tries. Try again in N minutes." and disable the Pay button until then.

## R3 Paying, and paying twice
1. WHEN a payment is approved THE SYSTEM SHALL, in one SQL transaction: record the payment, set `AmountPaid = TotalDue`, set `Status = PaidInFull`, and write an audit log entry and an outbox message `PaymentReceived`.
2. Each pay call SHALL carry an idempotency key. A retry with the same key SHALL return the first receipt and SHALL NOT record a second payment. The same key with a different settlement SHALL return `IDEMPOTENCY_MISMATCH`.
3. IF the buyer submits again for a settlement that is already `PaidInFull` (any key, any card) THEN THE SYSTEM SHALL return the existing receipt as a success and SHALL NOT record a second payment or check the card.
4. IF two pay calls race THEN exactly one payment SHALL be recorded and both calls SHALL return that receipt.
5. WHEN the buyer opens the pay page for a settlement that is already `PaidInFull` THE page SHALL go straight to the receipt instead of showing the form.

## R4 After paying
1. THE Purchases page SHALL show the row as "Paid in full" with the paid time, and the Pay button SHALL be replaced by a "Receipt" link.
2. THE receipt page SHALL show the amounts, the card label (`Demo card •••• 1234`), the payment reference and the paid time.
3. THE seller's Sales page SHALL show the row as "Paid in full".
4. THE notifier SHALL create one notification for the buyer ("Payment received for {title}") and one for the seller ("{buyer} paid for {title}"), each linking to the receipt or the sale.
5. WHILE a row is `PendingPayment` and not past its deadline THE Purchases page SHALL show the Pay button enabled.

## R5 Missed payment: default and strikes (design W12)
1. WHEN a settlement is still `PendingPayment` after its `PaymentDeadline` THE lifecycle leader SHALL, within 1 minute and in one fenced SQL transaction: set the settlement to `Defaulted`, set the auction outcome to `Defaulted`, add one strike to the buyer (`ident.UserStrikes`, one per settlement), write an audit log entry (`settlement.defaulted`), and write an outbox message `PaymentDefaulted` with the buyer's active strike count.
2. Active strikes SHALL be the user's strikes not cleared by an accepted appeal (R7.5).
3. WHEN the buyer's active strikes reach 3 THE SYSTEM SHALL, in the same transaction, set the buyer to `Suspended` with no end date (`SuspendedUntil = null`, "until an appeal is accepted"), revoke the buyer's sessions, and write an audit log entry (`user.suspend.strikes`).
4. THE suspension SHALL take effect within 1 second (status cache cleared, as for an admin suspension in spec 01): no sign-in, no bids, no listings (`ACCOUNT_SUSPENDED`).
5. A buyer already suspended SHALL NOT be suspended twice; each default SHALL still add its strike.
6. THE notifier SHALL create one notification for the seller ("The buyer did not pay for {title}", with a link to send a second-chance offer when R6.1 allows) and one for the buyer: "You got a strike for not paying for {title} ({n} of 3)" or, at 3, "Your account is suspended: 3 unpaid wins".
7. THE Purchases page SHALL show a defaulted row as "Not paid (deadline passed)" with no Pay button, and the account menu SHALL show the user's active strikes ("Strikes: 1 of 3") when there are any. THE seller's Sales page SHALL show it as "Buyer did not pay".

## R6 Second-chance offer (design W12)
1. WHEN a settlement is `Defaulted` and the auction had another distinct bidder THE seller SHALL be able to send one second-chance offer to the next highest distinct bidder, at that bidder's highest bid amount, within 7 days of the default.
2. IF there is no other bidder, or that bidder is suspended or banned, THEN `SendSecondChance` SHALL return `INVALID_STATE` and the Sales page SHALL not offer it.
3. THE offer SHALL last 48 hours. THE bidder SHALL get a notification "Second chance: buy {title} for {amount}" linking to the offer.
4. WHEN the bidder accepts in time THE SYSTEM SHALL create a new settlement for them (`IsSecondChance = true`, the offer amount as hammer price, premium and shipping as at close, deadline now + 72 h), set the auction outcome back to `Sold`, and notify the seller. The new settlement SHALL go through R1 to R5 like any other (pay, or default and strike).
5. WHEN the bidder declines, or the offer expires (checked by the lifecycle leader every minute), THE SYSTEM SHALL mark it Declined or Expired, notify the seller, and the seller MAY relist the item (spec 13).
6. Only one offer per defaulted settlement. Accept or decline twice SHALL return the first result.
## R7 Appeals
1. WHEN a suspended user tries to sign in THE login page SHALL show "Your account is suspended." and a "Write an appeal" link.
2. THE appeal page SHALL ask for username, password and a message (20 to 2000 characters). THE SYSTEM SHALL accept the appeal only IF the password is right and the user is `Suspended`. It SHALL NOT sign the user in.
3. A user SHALL have at most one open appeal. A second one while one is open SHALL return `ALREADY_APPLIED`. Wrong passwords SHALL count toward the login limit (spec 01).
4. THE admin page SHALL have an Appeals tab listing open appeals, newest first, with the user, the reason for the suspension (from the audit log), the strikes with their defaulted settlements, and the message.
5. WHEN an admin accepts an appeal THE SYSTEM SHALL set the user back to `Active` (as Reinstate in spec 06), clear the user's active strikes (kept as history, `ClearedAt` set), mark the appeal Accepted with the admin's note, and write an audit log entry. Defaulted settlements SHALL stay `Defaulted`.
6. WHEN an admin rejects an appeal THE SYSTEM SHALL keep the user suspended, mark the appeal Rejected with the admin's note (required), and write an audit log entry. The user MAY write a new appeal after that.
7. THE appeal page SHALL show the status of the user's latest appeal (Open, Accepted or Rejected, with the admin's note) after the user enters their username and password.

## R8 Observability
1. THE SYSTEM SHALL count `payments_total{result}` (`approved`, `invalid`, `repeat`, `rate_limited`, `rejected`) in the Api meter and `payments_defaulted_total`, `strikes_total`, `second_chance_total{result}` in the workers meter.
2. Payment logs SHALL carry the settlement id and the last 4 digits only.
