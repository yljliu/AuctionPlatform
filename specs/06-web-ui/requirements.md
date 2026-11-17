# 06 Web UI: Requirements

Covers the MudBlazor pages for the MVP. Design refs: v2 section 6 wireframes 6.1–6.4, 6.6–6.8, 6.10. Checkout (6.5) and moderation (6.9) are deferred.

## R1 Layout
1. THE SYSTEM SHALL show an app bar on every page with logo, search box, Categories menu, notification bell with unread count (signed in), and an avatar menu (My bids, Watchlist, Purchases, Seller hub for sellers, Admin for admins, Sign out) or Sign in / Register links.
2. THE SYSTEM SHALL show a footer badge with the Web instance name (e.g. `web-2`).
3. THE SYSTEM SHALL lay out grids as 3 columns at medium width and above, and 1 column on phones.

## R2 Browse and search (wireframe 6.1)
1. THE SYSTEM SHALL show auction cards with thumbnail, title, live price, bid count, countdown and a Watch toggle.
2. THE SYSTEM SHALL offer filters (status, price range, ending within, condition) and sort (ending soon, newest, price, most bids), kept in the URL query string so links can be shared.
3. WHILE loading THE SYSTEM SHALL show skeleton cards; WHEN nothing matches THE SYSTEM SHALL show "No auctions match" with a Reset button.
4. WHILE live prices are delayed THE SYSTEM SHALL show a "Prices may be delayed" chip.
5. Live cards SHALL update their price and countdown without reload.

## R3 Auction detail (wireframe 6.2)
1. THE SYSTEM SHALL show images, title, condition, seller summary, current price, countdown, bid count, reserve status, watcher count, and tabs for Description, Shipping & returns, and Bid history.
2. THE SYSTEM SHALL show the viewer's status: leading (with their max), outbid, or not bidding; and the minimum next bid.
3. THE SYSTEM SHALL offer three quick-bid buttons (min next, +1 increment, +3 increments) and a max-bid field with a Place Bid button.
4. WHEN the max bid is more than 3 times the current price THE SYSTEM SHALL ask for confirmation first.
5. WHILE a bid is sending, the auction is not Live, or the live feed is reconnecting THE SYSTEM SHALL disable the bid controls.
6. THE SYSTEM SHALL keep one idempotency key per bid attempt and reuse it on automatic retries.
7. WHEN a bid result arrives THE SYSTEM SHALL show a snackbar: success ("You're leading"), outbid by proxy ("Someone's max is higher. Price now $X"), or the error message with the correlation id.
8. WHEN the viewer is outbid by someone else THE SYSTEM SHALL show a warning snackbar with a one-click bid at the new minimum.
9. WHEN the auction is extended THE SYSTEM SHALL show "Extended +60s".
10. WHEN the auction is Sealed THE SYSTEM SHALL replace the bid form with "Closing… finalizing result"; WHEN Settled it SHALL show the outcome (Sold to handle at price, or Unsold).
11. WHEN the viewer is the seller THE SYSTEM SHALL hide the bid form and show "Your listing".
12. Signed-out viewers SHALL see a "Sign in to bid" button that returns them to the page after login.

## R4 My account (wireframe 6.4)
1. My bids SHALL list Active, Won and Lost tabs with item, my max, current price, status and time left.
2. Watchlist SHALL list watched auctions as cards.
3. Purchases SHALL list won auctions with the amount due and the payment deadline (payment itself deferred, button disabled "Coming soon").
4. Notifications SHALL list notifications newest first with mark-read and mark-all-read.

## R5 Seller hub (wireframes 6.6, 6.7)
1. New listing SHALL be a 4-step stepper: Item, Photos, Pricing & time, Review; each step validates before Next; drafts can be saved.
2. Pricing & time SHALL show the end time in the seller's local time and an estimated payout at the reserve (or starting) price.
3. Listings SHALL show tabs Draft, Scheduled, Live, Ended with actions per status (Edit, Schedule, Cancel, Relist, View).
4. Sales SHALL list sold auctions with buyer handle, total due and deadline.
5. Locked fields SHALL show a lock icon with a tooltip.

## R6 Admin (wireframe 6.8, MVP part)
1. THE SYSTEM SHALL show an Admin page only to admins, with live stats tiles, a health strip, the simulator panel (spec 08), and an auctions table with Force close, Freeze/Resume and Force cancel (each asks for a reason).
2. Users tab SHALL allow search and Suspend / Ban / Reinstate with a reason.

## R7 Demo helpers
1. WHEN `Demo:Enabled` is true THE SYSTEM SHALL show a quick-login drop-down on the sign-in page with the seeded users.

## R8 Errors and accessibility
1. EVERY error shown SHALL include its correlation id ("Ref: …").
2. Buttons and inputs SHALL have labels; colour SHALL not be the only signal (countdown red + icon).
