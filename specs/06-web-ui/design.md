# 06 Web UI: Design

## 1. Structure (`Auction.Web`)

```
Components/
  App.razor, Routes.razor
  Layout/ MainLayout.razor, AppBar.razor, Footer.razor, ReconnectModal.razor
  Shared/ AuctionCard.razor, AuctionCardLive.razor, Countdown.razor, MoneyText.razor,
          ErrorSnack.cs (helper), ConfirmDialog.razor, ReasonDialog.razor, LockIcon.razor
  Pages/
    Home.razor                /            → Browse with defaults
    Browse.razor              /c/{slug?}   (query: q, status, min, max, ending, cond, sort, page)
    AuctionPage.razor         /a/{id:guid}
    SignIn.razor, Register.razor  /signin, /register
    Account/ Bids.razor /me/bids, Watchlist.razor /me/watch, Purchases.razor /me/purchases, Notifications.razor /me/notifications
    Seller/ Listings.razor /sell, NewListing.razor /sell/new, EditListing.razor /sell/{itemId}, Sales.razor /sell/sales
    Admin/ Overview.razor /admin, Users.razor /admin/users, Auctions.razor /admin/auctions
  Bidding/ BidPanel.razor, BidHistory.razor, MyStatus.razor
Services/
  ApiClients.cs (typed gRPC clients), TokenStore.cs, AuthStateProvider.cs,
  LiveAuctionHub.cs (spec 04), ServerClock.cs, UserFeed.cs (spec 07), BidSender.cs
wwwroot/js/ clock.js, interop.js
```

Render mode: `InteractiveServer` globally. MudBlazor providers in `MainLayout`: `MudThemeProvider`, `MudDialogProvider`, `MudSnackbarProvider` (bottom-right, max 3), `MudPopoverProvider`.

## 2. Component map to wireframes

| Wireframe | Main MudBlazor parts |
|---|---|
| 6.1 Browse | `MudGrid`/`MudItem` (xs=12 md=4), `MudCard`, `MudSkeleton`, `MudDrawer` (filters, Responsive), `MudSelect`, `MudNumericField`, `MudChipSet`, `MudPagination` |
| 6.2 Detail | `MudCarousel` (images), `MudText`, `Countdown`, `MudChip` (reserve, extended), `MudButtonGroup` (quick bids), `MudNumericField` (max), `MudTabs`, `MudTable` (history, virtualized), `MudAlert` (reconnecting) |
| 6.3 Sign in | `MudCard`, `MudTextField`, `MudSelect` (quick login) |
| 6.4 Account | `MudTabs`, `MudDataGrid`, `MudList` (notifications) |
| 6.6 New listing | `MudStepper`, `MudForm` + validators, `MudFileUpload`, `MudDatePicker` + `MudTimePicker` |
| 6.7 Seller hub | `MudTabs`, `MudDataGrid` with action menu |
| 6.8 Admin | `MudPaper` tiles, `MudChart` (line, last 15 min), `MudDataGrid`, `ReasonDialog` |

## 3. BidPanel logic

```csharp
sealed class BidSender {
  Guid? pendingKey; decimal? pendingAmount;
  public async Task<BidOutcome> SendAsync(Guid auctionId, decimal max) {
    if (pendingKey is null || pendingAmount != max) { pendingKey = Guid.NewGuid(); pendingAmount = max; }
    for (int attempt = 0; attempt < 3; attempt++) {
      try { var r = await client.PlaceBidAsync(new() { AuctionId=…, MaxAmount=…, IdempotencyKey=pendingKey }, deadline: +2s);
            pendingKey = null; return BidOutcome.From(r); }
      catch (RpcException e) when (RpcErrors.IsRetryable(e)) { await Task.Delay(Jitter(200 * 2^attempt)); }
      catch (RpcException e) { pendingKey = null; return BidOutcome.From(RpcErrors.Detail(e)); }
    }
    return BidOutcome.Unknown;   // show "Couldn't confirm. Retry?" — retry reuses pendingKey
  }
}
```
Quick bid values: `min`, `min + inc(min)`, `min + 3·inc` (increment table sent in `AuctionDetail` as `increment_tiers`).
Confirm dialog when `max > 3 × current price` (or > 3 × starting price with no bids).

Snackbar text map (one place: `BidMessages.cs`): LEADING, OUTBID_BY_PROXY, MAX_RAISED, and each error code in spec 03.

Outbid-by-others snackbar: `AuctionLive` sees `bid_accepted` where previous leader == me and new leader != me.

## 4. Auth in UI
- `AuthStateProvider` builds `ClaimsPrincipal` from `TokenStore` (`Me` object). Role-gated pages use `[Authorize(Roles="Seller")]` / `"Admin"`.
- Sign-in page posts to a minimal API endpoint `/auth/signin` (form post, not interactive) so the auth cookie can be set; then redirects to `returnUrl`.
- Data Protection keys on volume `/keys` shared by web-1..3.

## 5. Browse URL state
`BrowseQuery` record ⇄ query string with `NavigationManager.GetUriWithQueryParameters`; filters change → navigate (replace: true) → `OnParametersSetAsync` reloads.

## 6. Seller stepper
- Step 1 Item: `MudForm` bound to `ItemDraft` with validators mirroring `ItemRules` (shared via Contracts? No: Web only references Contracts, so duplicate simple rules client-side; server is authoritative and its field errors are mapped back onto the form by field name).
- Step 2 Photos: `MudFileUpload` → `UploadImage` client stream (64 KB chunks), progress bar per file.
- Step 3 Pricing & time: start (date+time, local), duration select (5 min, 15 min, 1 h, 1 d, 3 d, 7 d), starting, reserve, anti-snipe fields (advanced expander). Estimated payout = price − price×sellerFee%.
- Step 4 Review: renders `AuctionPage` preview component in read-only mode. "Schedule" calls `CreateAuction`.

## 7. Styling
Default MudBlazor theme with one primary colour; dark mode toggle in avatar menu (`MudThemeProvider IsDarkMode`). Money via `MoneyText` (`CultureInfo("en-US")`, currency code from data).
