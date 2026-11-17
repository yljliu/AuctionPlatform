# 01 Foundation: Design

## 1. Repository layout

```
auction-platform/
  Auction.sln
  Directory.Build.props          # net8.0, nullable, LangVersion 12, common analyzers
  Directory.Packages.props       # central package versions
  .env.example
  deploy/
    compose.yml                  # full demo stack
    compose.lite.yml             # dev stack (low memory)
    proxy/yarp.json              # cookie affinity to web-1..3
    prometheus/prometheus.yml
    grafana/provisioning/...
  scripts/
    seed.ps1                     # calls Api admin seed RPC or runs SeedRunner
  src/
    Auction.Contracts/           # *.proto + generated C# (Grpc.Tools)
    Auction.Domain/              # pure C#: rules, state machine, money, increments, proxy resolver
    Auction.Infrastructure/      # EF Core DbContext, migrations, Redis client, Lua scripts, repositories
    Auction.Api/                 # gRPC services, interceptors, auth
    Auction.Web/                 # Blazor Server + MudBlazor
    Auction.Workers/             # hosted services: persister, lifecycle, outbox relay, notifier
    Auction.Simulator/           # bot runner + verifier (also hosts SimulatorService gRPC)
    Auction.Migrator/            # console: apply migrations, optional seed, exit
  tests/
    Auction.Domain.Tests/        # xUnit + FsCheck
    Auction.Integration.Tests/   # Testcontainers (MsSql, Redis)
    vectors/proxy.json           # shared proxy-bidding test cases (C# and Lua)
```

Dependency rule: `Domain` depends on nothing. `Infrastructure` → `Domain`, `Contracts`. `Api`, `Workers`, `Simulator`, `Migrator` → `Infrastructure`. `Web` → `Contracts` only (talks to Api through gRPC).

## 2. Packages

| Package | Used by | Why |
|---|---|---|
| `Grpc.AspNetCore`, `Grpc.Tools`, `Google.Protobuf` | Contracts, Api | gRPC server and codegen |
| `Grpc.Net.ClientFactory` | Web, Simulator | Typed gRPC clients |
| `Microsoft.EntityFrameworkCore.SqlServer` + `.Design` | Infrastructure, Migrator | ORM and migrations |
| `Dapper` | Infrastructure | Persister hot path |
| `StackExchange.Redis` | Infrastructure | Redis client |
| `BCrypt.Net-Next` | Api | Password hashing |
| `Microsoft.AspNetCore.Authentication.JwtBearer` | Api | JWT validation |
| `MudBlazor` | Web | UI components |
| `Yarp.ReverseProxy` | deploy/proxy (small host) | Sticky load balancing |
| `Serilog.AspNetCore`, `Serilog.Sinks.Seq` | all hosts | Logging |
| `OpenTelemetry.Extensions.Hosting`, `.Exporter.Prometheus.AspNetCore`, `.Instrumentation.AspNetCore`, `.Instrumentation.GrpcNetClient` | all hosts | Metrics and traces |
| `AspNetCore.HealthChecks.SqlServer`, `.Redis` | all hosts | Health checks |
| `xunit`, `FsCheck.Xunit`, `FluentAssertions`, `Testcontainers.MsSql`, `Testcontainers.Redis` | tests | Testing |

## 3. Compose services

| Service | Image / build | Ports | Healthcheck | Lite? |
|---|---|---|---|---|
| sqlserver | `mcr.microsoft.com/mssql/server:2022-latest` | 1433 | `sqlcmd -Q "SELECT 1"` | yes |
| redis | `redis:7.2` with `--appendonly yes --appendfsync everysec` | 6379 | `redis-cli ping` | yes |
| redis-replica | `redis:7.2 --replicaof redis 6379` | — | `redis-cli ping` | no |
| migrator | build `Auction.Migrator` | — | exits 0 | yes |
| api-1, api-2 | build `Auction.Api` | 7001 inside each container | `/health/ready` | api-1 only |
| workers-1, workers-2 | build `Auction.Workers` | 7101 (metrics, health) | `/health/ready` | workers-1 only |
| web-1, web-2, web-3 | build `Auction.Web` | 8080 inside; host 5001, 5002, 5003 | `/health/ready` | web-1 only |
| proxy | build `deploy/proxy` (YARP) | 5000 | `/health` | no |
| simulator | build `Auction.Simulator` | 7201 | `/health/ready` | no |
| seq | `datalust/seq` | 5341 (UI 8081) | — | yes |
| prometheus | `prom/prometheus` | 9090 | — | no |
| grafana | `grafana/grafana` | 3000 | — | no |

Volumes: `sql-data`, `redis-data`, `images` (api read-write at `/data/images`), `dp-keys` (all web copies at `/keys`, Data Protection keys).
`depends_on` uses `condition: service_healthy` for sql/redis and `service_completed_successfully` for migrator.
Each container gets `INSTANCE_NAME` (e.g. `web-2`) for logs and the footer badge.

Web to Api: `Api:Endpoints = ["http://api-1:7001", "http://api-2:7001"]`. Web creates one `GrpcChannel` with a static resolver and `round_robin` load balancing (`Grpc.Net.Client` `StaticResolverFactory`), so calls and streams spread across both Api copies and fail over if one is down. Lite mode lists only `api-1`.

## 4. Database (MVP tables)

Use EF Core code-first. Entities follow design 4.2 exactly; MVP creates these tables now and leaves the deferred ones out until needed:

- `ident.Users`, `ident.UserRoles`, `ident.RefreshTokens`, `ident.SellerProfiles`
- `catalog.Categories`, `catalog.Items`, `catalog.ItemImages`, `catalog.Auctions`, `catalog.BidIncrementTiers`, `catalog.FeeSchedules`
- `bidding.Bids`, `bidding.AuctionEvents`, `bidding.LifecycleLeases`, `bidding.Watchlist`
- `pay.Settlements`
- `notify.Outbox`, `notify.Notifications`
- `ops.AuditLog`, `ops.SimulationRuns`

Enum columns are `TINYINT` mapped with `HasConversion<byte>()`. Enums live in `Auction.Domain` (`UserStatus`, `Role`, `AuctionStatus`, `AuctionOutcome`, `ItemStatus`, `ItemCondition`, `BidKind`, `SettlementStatus`, `NotificationType`).

`AuctionStatus` values (must match design 2.2 and filtered index): `Draft=0, Scheduled=1, Live=2, Sealed=3, Settling=4, Settled=5, Cancelled=6, Frozen=7`.

## 5. Auth

### 5.1 Tokens
- Access token: JWT HS256, 10 min. Claims: `sub` (UserId), `name` (Username), `role` (one per role), `sv` (StatusVersion).
- Refresh token: 32 random bytes, base64url. Store only SHA-256 hash in `RefreshTokens.TokenHash`. `FamilyId` set at login; carried on rotation.
- Signing key from env `JWT__SigningKey` (≥ 32 bytes).

### 5.2 Web token custody
Blazor Server keeps tokens in a scoped `TokenStore` per circuit, plus an encrypted auth cookie (ASP.NET Data Protection, keys persisted to a shared volume so all 3 web copies can read it). Browser never sees the JWT.
A `DelegatingHandler` on the gRPC client adds `authorization: Bearer <access>`; on `UNAUTHENTICATED` it calls `RefreshToken` once and retries.

### 5.3 Interceptors (Api, in order)
1. `CorrelationInterceptor`: reads or creates `x-correlation-id` (= `Activity.TraceId`), adds to log scope and response trailers.
2. `ExceptionInterceptor`: maps `DomainException(code)` to status + `ErrorDetail`; unknown exceptions to `INTERNAL`.
3. ASP.NET auth middleware (JwtBearer) + `[Authorize(Roles=...)]` on service methods.
4. `RateLimitInterceptor`: per-method policies, Redis token bucket.

### 5.4 Status check for bids
`UserStatusCache.GetAsync(userId)` reads `{u:ID}:status` (JSON `{status, sv}`) from Redis, falls back to SQL and caches 10 min. Admin ban writes SQL then overwrites the key. Spec 03 calls it before running the bid script.

## 6. Error model

```csharp
public sealed class DomainException(string code, string message, IReadOnlyList<FieldError>? fields = null)
    : Exception(message) { public string Code { get; } = code; ... }

public static class ErrorCodes { public const string BelowMinimum = "BELOW_MINIMUM"; ... } // all of design 5.4
public static class ErrorMap  { public static (StatusCode status, bool retryable) For(string code) => ...; }
```

`ErrorDetail` proto (in `common.proto`):
```proto
message FieldError { string field = 1; string rule = 2; string message = 3; }
message ErrorDetail { string code = 1; string message = 2; bool retryable = 3; string correlation_id = 4;
  repeated FieldError fields = 5; int64 retry_after_ms = 6; }
```
Sent as a binary trailer `error-detail-bin`. Clients read it with a shared helper `RpcErrors.TryGetDetail(RpcException)`.

## 7. Rate limiter

Lua token bucket on key `rl:{scope}:{id}` (hash: `tokens`, `ts`). Args: capacity, refill per second, cost. Returns `allowed, retry_after_ms`. One script, used by both login (IP scope) and bids (user scope).

## 8. Seed data

`SeedRunner` (in Infrastructure; called by Migrator with `--seed` and by `scripts/seed.ps1`):
- Users: `admin`, `seller1..3`, `alice`, `bob`, `carol`, `dave`, `erin` (password from `SEED_PASSWORD` env). Sellers get Seller role and Verified `SellerProfiles`.
- Categories: Watches (Vintage, Modern), Art (Paintings, Prints), Collectibles (Coins, Cards).
- Increment table 1: from 0 → 1.00, 50 → 2.50, 250 → 5.00, 1000 → 10.00, 5000 → 50.00.
- Fee schedule 1: buyer premium 5%, seller final value 8%, listing fee 0.
- 6 auctions: 2 Live ending in 10 min, 2 Live ending in 1 h, 2 Scheduled starting in 5 min. Upsert by a stable `Slug`-like seed key so reruns do not duplicate.
