# 01 Foundation: Requirements

Covers: solution layout, local deployment, database and migrations, authentication, shared error model. Design refs: v2 sections 2.9, 3 (error model), 4.2 (`ident`), 5.4, 5.5.2, 7.3.

## R1 One-command local run
**User story:** As a developer, I want one command to start the platform, so that anyone can run the demo.

1. WHEN a developer runs `docker compose -f deploy/compose.lite.yml up -d` THE SYSTEM SHALL start `sqlserver`, `redis`, `migrator`, `api` (x1), `workers`, `web` (x1) and `seq`.
2. WHEN a developer runs `docker compose -f deploy/compose.yml up -d` THE SYSTEM SHALL additionally start `redis-replica`, a second `api`, a second `workers` (lifecycle standby), three `web` copies, `proxy`, `simulator`, `prometheus` and `grafana`.
3. THE SYSTEM SHALL start `api`, `workers` and `web` only after `sqlserver` and `redis` report healthy and `migrator` has exited with code 0.
4. THE SYSTEM SHALL read all secrets (SQL password, JWT signing key) from a `.env` file that is git-ignored, with `.env.example` committed.
5. WHEN `scripts/seed.ps1` runs THE SYSTEM SHALL create the admin user, 3 seller users, 5 bidder users, the category tree, the default increment table, one fee schedule and 6 demo auctions; running it twice SHALL NOT create duplicates.

## R2 Database schema and migrations
1. THE SYSTEM SHALL create schemas `ident`, `catalog`, `bidding`, `pay`, `notify`, `ops` through EF Core migrations.
2. THE SYSTEM SHALL store money as `DECIMAL(19,4)` and times as `DATETIME2(6)` UTC.
3. WHEN the `migrator` container starts THE SYSTEM SHALL apply pending migrations and exit 0, or exit non-zero and log the error.

## R3 Register
1. WHEN a visitor submits a username (3 to 32 chars, letters, digits, `_`), an email and a password (12+ chars) THE SYSTEM SHALL create a user with role Bidder and status Active and return `user_id`.
2. IF the username is taken THEN THE SYSTEM SHALL return `USERNAME_TAKEN`.
3. IF the email is taken THEN THE SYSTEM SHALL return the same success-shaped reply as R3.1 without creating a second account (no account enumeration).
4. IF any field fails its rule THEN THE SYSTEM SHALL return `VALIDATION_FAILED` with one entry per bad field.
5. THE SYSTEM SHALL store only a bcrypt hash (cost 12) of the password.

## R4 Login, refresh, logout
1. WHEN a user submits a correct email and password THE SYSTEM SHALL return an access token (10 min) and a refresh token (14 days) plus the `Me` object.
2. IF the email or password is wrong THEN THE SYSTEM SHALL return `INVALID_CREDENTIALS` and increment `FailedLogins`.
3. WHEN `FailedLogins` reaches 5 within 10 minutes THE SYSTEM SHALL set `LockedUntil` to now + 10 min and return `ACCOUNT_LOCKED` until then.
4. IF the user status is Banned THEN THE SYSTEM SHALL return `ACCOUNT_BANNED`.
5. WHEN a valid refresh token is used THE SYSTEM SHALL revoke it and return a new pair in the same family.
6. IF a revoked refresh token is used THEN THE SYSTEM SHALL revoke every token in its family and return `REFRESH_REUSED`.
7. WHEN a user logs out THE SYSTEM SHALL revoke the current refresh token.

## R5 Authorization
1. THE SYSTEM SHALL reject calls to signed-in methods without a valid access token with `UNAUTHENTICATED`.
2. THE SYSTEM SHALL reject calls that need a role the caller lacks with `ROLE_REQUIRED`.
3. WHEN an admin bans or suspends a user THE SYSTEM SHALL bump `StatusVersion`, revoke the user's refresh tokens, and cache `{u:ID}:status` in Redis so the next bid sees it within 1 second.

## R6 Error model
1. THE SYSTEM SHALL return every failure as a gRPC status plus an `ErrorDetail { code, message, retryable, correlation_id, fields[] }`.
2. THE SYSTEM SHALL map each error code to the gRPC status in design table 5.4.
3. THE SYSTEM SHALL attach the request's trace id as `correlation_id` and log it with the error.
4. IF an unhandled exception occurs THEN THE SYSTEM SHALL return `INTERNAL` without exposing the exception message.

## R7 Rate limits
1. THE SYSTEM SHALL limit `Login` to 10 calls per minute per IP and return `RATE_LIMITED` with `retry_after_ms` when exceeded.
2. THE SYSTEM SHALL limit `PlaceBid` to 5 calls per second per user (enforced in spec 03, using the limiter built here).

## R8 Health
1. THE SYSTEM SHALL expose `/health/live` and `/health/ready` on `api`, `web` and `workers`; `ready` SHALL fail when SQL or Redis is unreachable.
