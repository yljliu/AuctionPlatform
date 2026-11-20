# 10 Demo Payment: Requirements

Covers: paying for a won auction through a fake payment API, so a demo can go from "you won" to "paid". No real money moves. Replaces the disabled "Coming soon" Pay button from spec 06 R4.3. Installments, escrow, refunds, disputes and real payment providers are out of scope.

## R1 Who can pay
1. THE SYSTEM SHALL let only the buyer of a settlement pay it. Anyone else SHALL get `NOT_OWNER` (unknown id: `NOT_FOUND`).
2. THE SYSTEM SHALL accept a payment only WHILE the settlement is `PendingPayment` and its `PaymentDeadline` has not passed.
3. IF the settlement is already `PaidInFull` THEN THE SYSTEM SHALL return `INVALID_STATE` (unless it is a retry, see R3.2).
4. IF the deadline has passed THEN THE SYSTEM SHALL return `DEADLINE_PASSED`.

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

## R3 Paying
1. WHEN a payment is approved THE SYSTEM SHALL, in one SQL transaction: record the payment, set `AmountPaid = TotalDue`, set `Status = PaidInFull`, and write an audit log entry and an outbox message `PaymentReceived`.
2. Each pay call SHALL carry an idempotency key. A retry with the same key SHALL return the first result and SHALL NOT record a second payment. The same key with a different settlement SHALL return `IDEMPOTENCY_MISMATCH`.
3. IF two pay calls race THEN only one SHALL succeed; the other SHALL get `INVALID_STATE` (row version check).

## R4 After paying
1. THE Purchases page SHALL show the row as "Paid in full" with the paid time, and the Pay button SHALL be replaced by a "Receipt" link.
2. THE receipt page SHALL show the amounts, the card label (`Demo card •••• 1234`), the payment reference and the paid time.
3. THE seller's Sales page SHALL show the row as "Paid in full".
4. THE notifier SHALL create one notification for the buyer ("Payment received for {title}") and one for the seller ("{buyer} paid for {title}"), each linking to the receipt or the sale.
5. WHILE a row is `PendingPayment` THE Purchases page SHALL show the Pay button enabled.

## R5 Observability
1. THE SYSTEM SHALL count `payments_total{result}` (`approved`, `invalid`, `rejected`) in the Api meter.
2. Payment logs SHALL carry the settlement id and the last 4 digits only.
