# 06 Web UI: Tasks

- [ ] 1. Layout shell
  - MainLayout, AppBar (search, categories menu from `GetCategoryTree`, bell placeholder, avatar menu by role), Footer instance badge, MudBlazor providers, dark-mode toggle.
  - _Requirements: R1.1–R1.3_

- [ ] 2. Sign in, register, quick login
  - Form-post sign-in endpoint + cookie, returnUrl, Register page with server field errors, quick-login select when `Demo:Enabled`.
  - _Requirements: R3.12, R7.1, R8.1_

- [ ] 3. Browse page (static)
  - Filters drawer, sort, paging, URL state, skeletons, empty state, delayed-prices chip, AuctionCard.
  - _Requirements: R2.1–R2.4_

- [ ] 4. Auction page (static)
  - Images, details, tabs, history table, seller summary, MyStatus, seller view, sign-in-to-bid.
  - _Requirements: R3.1, R3.2, R3.11, R3.12_

- [ ] 5. Live wiring (after spec 04 task 6)
  - AuctionLive on detail; AuctionCardLive on browse; Countdown with ServerClock; reconnect alert.
  - _Requirements: R2.5, R3.5, R3.9, R3.10_

- [ ] 6. BidPanel
  - BidSender with idempotency reuse and retries, quick bids, max field, 3× confirm, snackbars (incl. outbid-by-others with one-click bid), disabled states.
  - Check by hand: double-click Place Bid → one bid; stop api → retry message; restart → retry succeeds once.
  - _Requirements: R3.3–R3.8, R8.1_

- [ ] 7. My account pages
  - Bids tabs, Watchlist, Purchases (amount due, deadline, disabled Pay), Notifications list.
  - _Requirements: R4.1–R4.4_

- [ ] 8. Seller hub
  - Listings tabs with actions, New listing stepper (4 steps, server field errors mapped to form), Edit listing with lock icons, Sales list.
  - _Requirements: R5.1–R5.5_

- [ ] 9. Admin pages (shell; simulator panel filled in spec 08)
  - Overview tiles + health strip + chart; Auctions grid with Force close / Freeze / Resume / Force cancel using ReasonDialog; Users search with Suspend / Ban / Reinstate.
  - _Requirements: R6.1, R6.2_

- [ ] 10. UI checks
  - bUnit tests: BidPanel disabled states; 3× confirm; snackbar text per code. Playwright smoke: sign in → browse → open auction → bid → see leading.
  - _Requirements: R3, R8.2_
