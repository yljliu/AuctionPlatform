# AuctionPlatform
Design and Develop a distributed auction platform including the frontend, database, and backend engine. 

## What you need

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (check with `dotnet --list-sdks`)
- [Docker Desktop](https://www.docker.com/products/docker-desktop/) (it must be running)
- About 3 GB of free memory for the dev stack

## 1. Make your `.env` file (once)

Copy the example and put in your own values. Everyone picks their own; the file is never committed.

```
copy .env.example .env
notepad .env
```

| Name | What it is | Rule |
|---|---|---|
| `SA_PASSWORD` | Password for the SQL Server admin account (`sa`) | 8+ characters with 3 of: upper, lower, digit, symbol. No `;` |
| `JWT__SigningKey` | Secret used to sign login tokens | 32+ random characters |
| `SEED_PASSWORD` | Password for the demo users | 12+ characters |

Make a random signing key:

```
[Convert]::ToBase64String((1..48 | % { Get-Random -Max 256 }) -as [byte[]])
```

## 2. Build the code

```
dotnet build
```

## 3. Start the website (dev stack)

Run from the repo folder:

```
docker compose --env-file .env -f deploy/compose.lite.yml up -d --build
```

Check that it is up. `migrator` should say `Exited (0)` and the rest `healthy`:

```
docker compose --env-file .env -f deploy/compose.lite.yml ps -a
```

## 4. Add demo data (optional)

```
powershell -File scripts\seed.ps1
```

This adds `admin`, `seller1` to `seller3`, and `alice`, `bob`, `carol`, `dave`, `erin`. Log in with `<name>@demo.local` and your `SEED_PASSWORD`. It is safe to run more than once.

## 5. Open it

| What | Address |
|---|---|
| Website | http://localhost:5001 |
| Logs (Seq) | http://localhost:8081 |

Register a new account, or log in with a demo user. Your name shows in the top bar.

## 6. Stop it

Stop and keep your data:

```
docker compose --env-file .env -f deploy/compose.lite.yml stop
```

Wipe the database too (needed after you change `SA_PASSWORD`):

```
docker compose --env-file .env -f deploy/compose.lite.yml down -v
```

## Full stack 

3 web copies behind a proxy, 2 Api copies, Redis replica, Prometheus and Grafana:

```
docker compose --env-file .env -f deploy/compose.yml up -d --build
```

| What | Address |
|---|---|
| Website (through the proxy) | http://localhost:5000 |
| Grafana | http://localhost:3000 |
| Prometheus | http://localhost:9090 |
| Logs (Seq) | http://localhost:8081 |

## Folders

| Folder | What is in it |
|---|---|
| `src/` | The app: Api, Web, Workers, Domain, Infrastructure, Contracts, Migrator, Simulator |
| `tests/` | Unit and integration tests |
| `deploy/` | Docker compose files, proxy, Prometheus and Grafana config |
| `scripts/` | Helper scripts (`seed.ps1`) |
| `specs/` | Requirements, design and task lists for each part |
