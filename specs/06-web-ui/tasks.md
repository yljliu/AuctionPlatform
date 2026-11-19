# 06 Web UI: Tasks

- [ ] 1. Layout shell
  - MainLayout, AppBar (search, categories menu from `GetCategoryTree`, bell placeholder, avatar menu by role), Footer instance badge, MudBlazor providers, dark-mode toggle.
  - _Requirements: R1.1–R1.3_
  - Status: code done; check by eye.

- [ ] 2. Sign in, register, quick login
  - Form-post sign-in endpoint + cookie, returnUrl, Register page with server field errors, quick-login select when `Demo:Enabled`.
  - _Requirements: R3.12, R7.1, R8.1_
  - Status: code done; check by eye (quick login needs SEED_PASSWORD in .env).

- [ ] 3. Browse page (static)
  - Filters drawer, sort, paging, URL state, skeletons, empty state, delayed-prices chip, AuctionCard.
  - _Requirements: R2.1–R2.4_
  - Status: code and unit tests done (`PageModelTests`).

- [ ] 4. Auction page (static)
  - Images, details, tabs, history table, seller summary, MyStatus, seller view, sign-in-to-bid.
  - _Requirements: R3.1, R3.2, R3.11, R3.12_
  - Status: code done; check by eye.

- [ ] 5. Live wiring (after spec 04 task 6)
  - AuctionLive on detail; AuctionCardLive on browse; Countdown with ServerClock; reconnect alert.
  - _Requirements: R2.5, R3.5, R3.9, R3.10_
  - Status: code done; check by eye.

- [ ] 6. BidPanel
  - BidSender with idempotency reuse and retries, quick bids, max field, 3× confirm, snackbars (incl. outbid-by-others with one-click bid), disabled states.
  - Check by hand: double-click Place Bid → one bid; stop api → retry message; restart → retry succeeds once.
  - _Requirements: R3.3–R3.8, R8.1_
  - Status: code and unit tests done (`BidSenderTests`, `BidRulesTests`, `BidPanelTests`); the checks by hand are yours.

- [ ] 7. My account pages
  - Bids tabs, Watchlist, Purchases (amount due, deadline, disabled Pay), Notifications list.
  - _Requirements: R4.1–R4.4_
  - Status: code done; Watchlist and Notifications show an empty page until spec 07 adds their calls.

- [ ] 8. Seller hub
  - Listings tabs with actions, New listing stepper (4 steps, server field errors mapped to form), Edit listing with lock icons, Sales list.
  - _Requirements: R5.1–R5.5_
  - Status: code and unit tests done (`PageModelTests`); check by eye.

- [ ] 9. Admin pages (shell; simulator panel filled in spec 08)
  - Overview tiles + health strip + chart; Auctions grid with Force close / Freeze / Resume / Force cancel using ReasonDialog; Users search with Suspend / Ban / Reinstate.
  - _Requirements: R6.1, R6.2_
  - Status: code done; integration tests written (`WebUiQueriesTests`), waiting for your run.

- [ ] 10. UI checks
  - bUnit tests: BidPanel disabled states; 3× confirm; snackbar text per code. Playwright smoke: sign in → browse → open auction → bid → see leading.
  - _Requirements: R3, R8.2_
  - Status: bUnit and unit tests done (104 pass); Playwright smoke test written (`SmokeTests`), waiting for your run.

## Build notes (differences from design.md)

- Sign in stays at `/account/login` (a plain server page that sets the cookie, from spec 01) instead of `/signin` plus a separate endpoint. Quick login posts only the user name; the server adds `Demo:Password` (compose passes `SEED_PASSWORD`), so the password never reaches the browser.
- The card component is `AuctionTile.razor`, not `AuctionCard.razor`, because the proto already has a type named `AuctionCard`. Live tiles follow the shared feed directly, so there is no separate `AuctionCardLive`.
- `AuctionLive` is now the live provider: it follows the feed and renders its content with the newest view, and `Changed` gives (before, after) for the outbid-by-others snackbar. The page puts the price, `BidPanel` and history inside it. `data-feed` on `#live-panel` shows the feed status for browser tests.
- The new listing stepper is a simple step header plus Back/Next, not `MudStepper`, and photos use `InputFile`, to avoid MudBlazor 7 API changes. The item is saved as a draft when leaving step 1; `?item={id}` continues a draft at step 3 (also used for Schedule and Relist).
- Forms reuse the `ItemRules` limits from Auction.Domain (the Web reaches Domain through Contracts), so the client checks cannot drift from the server.
- Estimated payout uses `Fees:SellerFinalValuePct` (default 8, the seeded fee schedule), because the Api does not send fee percentages.
- New Api calls the pages need: `BiddingService.ListMyPurchases`, `SellerService.ListMySales` (both return `SettlementRow`s), and on `AdminService`: `ReinstateUser`, `SearchUsers`, `ListAuctions` (any status, Frozen and Cancelled too) and `GetStats` (tiles, bids per minute, SQL and Redis health, lifecycle leader).
- The Watch toggle on cards only shows the state, and the bell count is 0, until spec 07 adds the watchlist and notification calls.
- `BidSender` holds a lock per page, so a double click waits and can never send two bids at once. A rate limit is not retried automatically.
- Shown times are UTC except on the listing forms, which use the browser time zone (`auctionClock.tzOffsetMinutes`).
- The Spec 04 latency test now makes its auction in SQL as well as Redis (`StackData`), because the full auction page loads the auction from SQL. It needs `E2E_SQL`.
