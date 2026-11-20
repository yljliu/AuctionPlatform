# 12 Disputes, Refunds and Ratings: Requirements

Covers: disputes and refunds (design W16) and ratings (W18). Needs spec 11 (escrow, delivery, return window).

## R1 Opening a dispute (W16)
1. THE buyer SHALL be able to open one dispute on a settlement WHILE it is `PaidInFull` and either shipped but not delivered for 7+ days ("not received"), or delivered and inside the return window ("not as described", "damaged", "other").
2. A dispute SHALL have a reason (from that list), a message (20 to 2000 characters) and up to 3 photos (spec 02 image rules).
3. WHEN a dispute opens THE SYSTEM SHALL set the settlement to `Disputed`, which stops the payout (spec 11 R5.1), and notify the seller and the admins.
4. A second dispute on the same settlement SHALL return `ALREADY_APPLIED`. Outside the allowed time SHALL return `DEADLINE_PASSED`; anyone but the buyer `NOT_OWNER`.

## R2 Talking and deciding
1. Buyer, seller and admins SHALL see one message thread per dispute; buyer and seller MAY each add messages (and up to 3 photos each) WHILE it is open.
2. THE seller SHALL be able to offer a full refund at once ("Refund buyer"), which closes the dispute as Refunded without an admin.
3. An admin SHALL decide an open dispute: refund in full, refund part (an amount above 0 and below the total), or reject. A note is required.
4. Each side SHALL be notified of every new message and of the decision.
5. IF the seller has not replied in 3 days THEN the dispute SHALL be flagged "Seller has not replied" in the admin queue.

## R3 Refunds
1. A full refund SHALL move the whole escrow back to the buyer: to the wallet if they paid with it, otherwise back to `External` as a "card refund (demo)". THE settlement SHALL become `Refunded`, and the seller gets nothing.
2. A part refund SHALL move that amount to the buyer the same way, and the rest SHALL be paid out to the seller (minus the seller fee on the rest) at once. THE settlement SHALL become `Released`.
3. A rejected dispute SHALL set the settlement back to `PaidInFull`; the payout SHALL then follow spec 11 R5 (at once if the window has closed).
4. Every refund SHALL be balanced ledger entries with an idempotency key (spec 11 R1), so a decision applied twice moves money once.

## R4 Ratings (W18)
1. WHEN a settlement is delivered THE buyer and THE seller SHALL each be able to leave one rating of the other: 1 to 5 stars and a comment (0 to 500 characters), within 30 days of delivery.
2. A second rating by the same person for the same settlement SHALL return `ALREADY_RATED`. After 30 days SHALL return `DEADLINE_PASSED`.
3. A rating SHALL NOT be changed after it is sent. Admins MAY hide a rating's comment (the stars still count), with an audit entry.
4. A job SHALL update the seller's `RatingSum` and `RatingCount` (`SellerProfiles`) from their ratings within 1 minute; the auction page seller box SHALL show the average and count (as today).
5. THE seller's public ratings (stars, comment, buyer handle, date) SHALL be listed on `/sellers/{handle}`. Ratings of buyers SHALL be visible only to sellers who sold to them, on the Sales page.
6. THE other side SHALL be notified "{name} rated you {n} stars".

## R5 Observability
1. THE SYSTEM SHALL count `disputes_opened_total{reason}`, `disputes_closed_total{result}` and `ratings_total{role}`.
