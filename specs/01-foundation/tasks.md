# 01 Foundation: Tasks

- [x] 1. Create solution and projects
  - Create `Auction.sln`, all `src/` and `tests/` projects from design Ã‚Â§1, `Directory.Build.props`, `Directory.Packages.props`.
  - Add project references per the dependency rule. `dotnet build` passes.
  - _Requirements: R1_

- [x] 2. Compose lite stack with SQL and Redis
  - Write `deploy/compose.lite.yml` with `sqlserver`, `redis` (AOF on), `seq`, health checks and volumes.
  - Write `.env.example` (`SA_PASSWORD`, `JWT__SigningKey`, `SEED_PASSWORD`); add `.env` to `.gitignore`.
  - Check: `docker compose -f deploy/compose.lite.yml up -d` Ã¢â€ â€™ both healthy.
  - _Requirements: R1.1, R1.4_

- [x] 3. Domain enums and money helpers
  - Add enums from design Ã‚Â§4 in `Auction.Domain`.
  - Add `Money` value type (`decimal Amount`, `string Currency`), `ToCents()/FromCents()`, and proto conversion in Contracts.
  - Unit tests: cents round-trip, 4-decimal rounding rule (banker's rounding off; use `MidpointRounding.AwayFromZero`).
  - _Requirements: R2.2_

- [x] 4. DbContext, entities and first migration
  - `AuctionDbContext` with MVP tables from design Ã‚Â§4, schemas, decimal precision, `DATETIME2(6)`, rowversion, indexes and filtered indexes from v2 Ã‚Â§4.2.
  - Migration `0001_Initial`. `Auction.Migrator` applies it and exits 0.
  - Add `migrator` to compose lite with `service_completed_successfully`.
  - Integration test: migrate an empty Testcontainers SQL Server, assert all tables exist.
  - _Requirements: R2.1Ã¢â‚¬â€œR2.3, R1.3_

- [x] 5. Seed runner
  - Implement `SeedRunner` per design Ã‚Â§8, idempotent by stable keys. `scripts/seed.ps1` runs `docker compose run --rm migrator --seed`.
  - Test: run twice, row counts unchanged.
  - _Requirements: R1.5_

- [x] 6. Error model
  - `common.proto` (`Money`, `ErrorDetail`, `FieldError`), `ErrorCodes`, `ErrorMap`, `DomainException`, `ExceptionInterceptor`, client helper `RpcErrors`.
  - Unit test: each code maps to the status in v2 table 5.4.
  - _Requirements: R6.1Ã¢â‚¬â€œR6.4_

- [x] 7. Api host skeleton
  - `Auction.Api`: gRPC, Serilog (console + Seq), OTel (traces + Prometheus `/metrics`), health checks (`/health/live`, `/health/ready` with SQL + Redis), `CorrelationInterceptor`.
  - Add `api` to compose lite.
  - _Requirements: R8.1, R6.3_

- [x] 8. AuthService: Register and Login
  - `auth.proto` (Register, Login, RefreshToken, Logout, GetMe). Implement Register (validation, bcrypt 12, no enumeration) and Login (lockout, banned check, token issue).
  - Integration tests: success, `USERNAME_TAKEN`, duplicate email returns success shape, wrong password, lockout after 5, banned.
  - _Requirements: R3.1Ã¢â‚¬â€œR3.5, R4.1Ã¢â‚¬â€œR4.4_

- [x] 9. Refresh rotation and logout
  - Implement RefreshToken (rotate in family, reuse detection revokes family) and Logout.
  - Tests: rotate OK; reuse old token Ã¢â€ â€™ `REFRESH_REUSED` and new token now fails too.
  - _Requirements: R4.5Ã¢â‚¬â€œR4.7_

- [x] 10. Authorization and user status cache
  - JwtBearer config, role policies, `UserStatusCache` (Redis then SQL), `AdminService.BanUser/SuspendUser` minimal (writes SQL, bumps `StatusVersion`, revokes tokens, updates Redis key, writes `AuditLog`).
  - Test: ban Ã¢â€ â€™ next status read returns Banned within 1 s.
  - _Requirements: R5.1Ã¢â‚¬â€œR5.3_

- [x] 11. Rate limiter
  - Redis token-bucket Lua, `RateLimitInterceptor` with policy for Login (10/min/IP). Expose `IRateLimiter` for spec 03.
  - Test: 11th login in a minute Ã¢â€ â€™ `RATE_LIMITED` with `retry_after_ms > 0`.
  - _Requirements: R7.1, R7.2_

- [x] 12. Web host skeleton with auth
  - `Auction.Web`: Blazor Server, MudBlazor, layout shell, Login and Register pages, `TokenStore`, auth cookie, Data Protection keys on shared volume, gRPC client with auth handler and auto-refresh.
  - Check: register, log in, see username in app bar; refresh page stays signed in.
  - _Requirements: R3, R4 (UI)_

- [x] 13. Workers host skeleton
  - `Auction.Workers`: generic host, Serilog, OTel, health endpoints on 7101. No workers yet.
  - _Requirements: R8.1_

- [ ] 14. Full compose and proxy (files written and `docker compose config` passes; full-stack run not done yet, needs about 6 GB free memory)
  - `deploy/compose.yml`: everything in design Ã‚Â§3. YARP proxy with cookie affinity to web-1..3. `prometheus.yml` scrapes api, workers, web, simulator.
  - Check: open `http://localhost:5000`, log in, footer badge shows instance name; reload many times, same instance.
  - _Requirements: R1.2_

## Build notes (differences from design.md)

- Api listens on two ports: 7001 for gRPC (HTTP/2) and 7080 for `/health/*` and `/metrics` (HTTP/1).
- Correlation id is set by `CorrelationMiddleware`, not an interceptor. Same behavior.
- Role and sign-in checks use `[SignedIn]` / `[RequireRole]` in `AuthorizationInterceptor`, not `[Authorize]`, so callers get `UNAUTHENTICATED` / `ROLE_REQUIRED` with an `ErrorDetail`.
- `ident.Users` gained `FirstFailedLoginAt` (migration `LoginFailureWindow`) for the "5 failures in 10 minutes" rule.
- Web keeps tokens in Redis (encrypted), keyed by a session id in the auth cookie. This lets every web copy see the newest refresh token.
- Web sends the browser IP to the Api in `x-client-ip` so the login limit is per user. The Api port must not be public.
- Containers check health with `dotnet <app>.dll --probe <url>` (no curl in the images).
- Integration tests use `TEST_SQL` and `TEST_REDIS` when set, otherwise Testcontainers.
