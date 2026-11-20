# 11 Wallet, Checkout and Fulfilment: Requirements

Covers: the wallet (design W10), the rest of checkout (W11: address, pay with wallet, escrow) and fulfilment and payout (W13). Builds on spec 10 (card payments, settlements). Money is fake: deposits use the spec 10 fake card check. Wallet holds before bidding (design D2), installments and real payouts to banks are out of scope.

## R1 Ledger
1. THE SYSTEM SHALL record every money movement as balanced ledger entries (`pay.LedgerEntries`): each movement has entries that sum to zero across accounts.
2. Accounts SHALL be: one wallet per user, one escrow per settlement, platform fees, and the outside world (`External`, for fake card money in and payouts out).
3. A balance SHALL be the sum of that account's entries. THE SYSTEM SHALL NOT store a balance that can drift from the entries.
4. Every movement SHALL carry an idempotency key; a repeat SHALL return the first result and add no entries.

## R2 Wallet (W10)
1. A signed-in user SHALL see their wallet balance and the last 50 movements on `/me/wallet`.
2. WHEN a user deposits an amount (1.00 to 10,000.00, 2 decimals) with a fake card that passes the spec 10 card check THE SYSTEM SHALL move it from `External` to their wallet. A bad card SHALL return `VALIDATION_FAILED` with field issues and add nothing. The spec 10 invalid-card limit SHALL apply.
3. WHEN a user withdraws an amount THE SYSTEM SHALL allow it only up to their balance (`INSUFFICIENT_FUNDS` otherwise), move it from their wallet to `External`, and record a payout row "Paid to your bank (demo)".
4. Two withdrawals at once SHALL never take the balance below zero.

## R3 Checkout (rest of W11)
1. Before paying, THE buyer SHALL confirm a shipping address (name, line 1, line 2, city, region, postal code, country). THE SYSTEM SHALL keep a copy on the settlement (`ShippingSnapshot`) so later edits to the address book do not change it. Items with `LocalPickupOnly` SHALL skip this step.
2. THE buyer MAY save addresses (`/me/addresses`, at most 5, one default).
3. THE pay page SHALL offer "Pay with wallet" WHEN the wallet balance covers the total due, besides the spec 10 card.
4. WHEN the buyer pays with the wallet THE SYSTEM SHALL move the total due from their wallet to the settlement's escrow, in the same transaction as the spec 10 `PaidInFull` change. A card payment SHALL move the total due from `External` to escrow.
5. THE money SHALL stay in escrow until payout (R5) or refund (spec 12).
6. THE seller's "buyer paid" notification (spec 10) SHALL say "Ship {title} to {buyer}" and link to the sale.

## R4 Shipping and delivery (W13)
1. WHEN a settlement is `PaidInFull` THE seller SHALL be able to mark it shipped with a carrier (from a list) and a tracking number (4 to 40 letters and digits). Local-pickup items SHALL be marked "Picked up" instead, with no tracking.
2. THE buyer SHALL be notified "{title} has shipped" with the carrier and tracking number.
3. WHEN the buyer confirms delivery, or 14 days after shipping with no confirmation (checked by the lifecycle leader every minute), THE SYSTEM SHALL mark it delivered and notify both sides.
4. IF the item has `ReturnsAccepted` THEN a return window of `ReturnWindowDays` SHALL open at delivery; otherwise there is no window.
5. Only the seller marks shipped; only the buyer confirms delivery (`NOT_OWNER`). Marking shipped twice SHALL return the first result; a wrong state SHALL return `INVALID_STATE`.

## R5 Payout (W13)
1. WHEN the return window closes (or at delivery, when there is none) with no open dispute (spec 12) THE lifecycle leader SHALL pay out: move the escrow to the seller's wallet minus `SellerFee`, and the fee to platform fees, in one transaction, and set the settlement `Released`.
2. THE seller SHALL be notified "You were paid {amount} for {title}".
3. A payout SHALL happen once per settlement, even after a crash or two leaders (fenced, idempotency key = settlement id).
4. THE Sales page SHALL show each sale's stage: Awaiting payment, Paid (ship it), Shipped, Delivered (return window until …), Paid out, Disputed.
5. THE Purchases page SHALL show the same stages from the buyer's side, with "Confirm delivery" while Shipped.

## R6 Observability
1. THE SYSTEM SHALL count `wallet_movements_total{kind}` and `payouts_total`, and expose `escrow_held` (sum of escrow balances) as a gauge.
2. A nightly check (and a unit-tested query) SHALL confirm all ledger entries sum to zero; a non-zero sum SHALL log Critical.
