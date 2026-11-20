# Auction Platform: Spec Roadmap (MVP by Thursday 2026-11-19)

Source design: `Auction_Platform_Design_v2.md`. Beginner terms: `Auction_Platform_Beginners_Guide.md`.

Each spec folder has three files, done in this order:
1. `requirements.md`: user stories and acceptance criteria in EARS format ("WHEN x THE SYSTEM SHALL y").
2. `design.md`: how it is built. Classes, tables, keys, messages, flows.
3. `tasks.md`: small, checkable coding steps. Each step names the requirements it covers.

## 1. MVP scope

Core goal: **many users on separate web copies bid live, see the same price within 500 ms, and no accepted bid is lost.**

| In the MVP (by Thursday) | Deferred |
|---|---|
| Register, login, JWT access + refresh, roles (seeded admin and sellers) | Email verification, password reset, TOTP, seller applications |
| Categories, items, auctions, one image per item (disk volume) | MinIO, multiple images, collections, addenda |
| Browse, filters, `LIKE` search, offset paging, auction detail | Full-text index, keyset paging |
| Bid Lua script: proxy bidding, increment tiers, anti-sniping with cap, idempotency, own-lot check | Wallet holds (D2), Buy It Now (D8) |
| Redis primary + replica, `WAIT 1 50` | Sentinel and failover |
| Persister: stream to SQL, ordered, idempotent, `XAUTOCLAIM` | Multiple partitions |
| Live: gRPC stream Api to Web, SignalR to browser, catch-up by seq, snapshot fallback | Advanced coalescing |
| Lifecycle: open, closing-soon, seal, drain, settle (Sold or Unsold), lease + fencing | Second-chance, non-payment strikes |
| Settlement row at close (amount due shown to winner and seller) | Checkout, payments, payouts, shipping, disputes, ratings |
| Watchlist, in-app notifications (outbid, won, lost, ending soon) | Email, preferences, digests |
| Admin: simulator controls, live stats, force-close, ban | Moderation queue, audit UI |
| Simulator: `steady`, `closing-spike`, `snipe-war` + verifier | `redis-failover`, `reconnect-storm` |
| Health checks, Serilog to Seq, OTel metrics to Prometheus, one Grafana board | Alertmanager |
| `compose.yml` (full) and `compose.lite.yml` (dev) | Cloud deploy |

## 2. Specs and order

| # | Spec | Depends on | Day |
|---|---|---|---|
| 01 | `01-foundation` | none | Sun eve, Mon AM |
| 02 | `02-catalog` | 01 | Mon PM |
| 03 | `03-bidding-engine` | 01, 02 | Tue AM |
| 04 | `04-live-updates` | 03 | Tue PM |
| 05 | `05-auction-lifecycle` | 03 | Wed AM |
| 06 | `06-web-ui` | 02, 03, 04 | Mon to Wed, alongside |
| 07 | `07-watchlist-notifications` | 04, 05 | Wed PM |
| 08 | `08-simulator-admin` | 03, 04, 05 | Thu AM |
| 09 | `09-observability` | 01 | Hooks from Mon, board Thu PM |

## 3. Day plan

| Day | Work | Done when |
|---|---|---|
| Sun 11-15 eve | 01 tasks 1 to 5 | `docker compose -f compose.lite.yml up` shows SQL and Redis healthy; migrations applied |
| Mon 11-16 | 01 auth and errors; 02; 06 layout, browse, detail (read-only) | Seller logs in, creates an auction, it shows in browse |
| Tue 11-17 | 03 domain + Lua + persister; 04; 06 bid panel | Two browsers on different web copies bid and both see the price live |
| Wed 11-18 | 05; 07; 06 account pages | Auctions open, extend, end and settle with the right winner |
| Thu 11-19 | 08; 09 board; demo rehearsal | Simulator `closing-spike` run reports PASS; demo script runs end to end |

Cut order if late: 07 → Grafana board → `snipe-war` scenario → image upload (use a placeholder image).

## 4. Conventions

- .NET 8, C# 12, nullable enabled. `TreatWarningsAsErrors` in `Auction.Domain`.
- Projects: `Auction.Contracts`, `Auction.Domain`, `Auction.Infrastructure`, `Auction.Api`, `Auction.Web`, `Auction.Workers`, `Auction.Simulator`; tests in `tests/`.
- Money: `decimal` in C#, `DECIMAL(19,4)` in SQL, `Money` in protobuf, integer **cents** in Redis and Lua (Lua numbers are doubles; cents stay exact below 2^53).
- Time: UTC everywhere. Auction rules use Redis `TIME` only.
- Retryable writes take an `idempotency_key`.
- Errors use codes from design section 5.4 via `ErrorDetail`.
- A task is done when its tests pass and its acceptance criteria are checked.
- Commit after each task: `feat(<spec>): <task title>`.

## 5. MVP decisions

| ID | Decision |
|---|---|
| D1 | One Redis primary + one replica. No Sentinel. Hash tags in all keys. |
| D2 | Deferred. Eligibility = signed in, Active, not the seller. |
| D3 | Every bid is a max bid. |
| D4 | Accepted. Admin page shows replica status. |
| D5 | Price rises normally; reserve checked only at close. |
| D6 | Payment deadline 72 h shown on the settlement; not enforced in MVP. |
| D8 | No Buy It Now. |
| D12 | Blazor Server. |

## 6. Risks

| Risk | Plan |
|---|---|
| Proxy logic bugs | Write rules in C# `ProxyResolver` first with tests; Lua must match it (shared vectors in `tests/vectors/proxy.json`). |
| Blazor Server sticky sessions across 3 copies | YARP cookie affinity; prove with a hello page on Monday. |
| Low host memory (SQL needs ~2 GB) | Develop on `compose.lite.yml`; full stack only for the demo. |
| Time | Cut order above. |

## 5. After the MVP: the remaining design workflows

| # | Spec | Design workflows | Depends on |
|---|---|---|---|
| 10 | `10-demo-payment` | W11 (card pay), W12 (default, 3 strikes, second-chance offer), appeals | 05, 07 |
| 11 | `11-wallet-fulfilment` | W10, W11 (address, wallet, escrow), W13 | 10 |
| 12 | `12-disputes-ratings` | W16, W18 | 11 |
| 13 | `13-moderation-relist` | W15 (reports, queue, warn, remove, void bid), W14 (relist) | 03, 05, 10 |
| 14 | `14-accounts` | W7 (email verify), W8, W9 | 01, 11 (address fields) |

Branches stack as before: `feat/10-demo-payment` → `feat/11-…` → … → `feat/14-…`.
Still deferred: wallet holds before bidding (D2), installments, real email and payment providers, full-text search and keyset paging (W2), email digests (W5), `reconnect-storm` and `redis-failover` scenarios (W6), TOTP.
