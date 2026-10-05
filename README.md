# Salon Tracker (.NET)

[![CI](https://github.com/AtakanUk/salon-tracker-dotnet/actions/workflows/ci.yml/badge.svg)](https://github.com/AtakanUk/salon-tracker-dotnet/actions/workflows/ci.yml)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)
![PostgreSQL 17](https://img.shields.io/badge/PostgreSQL-17-336791)
[![License: MIT](https://img.shields.io/badge/license-MIT-green)](LICENSE)

The ASP.NET Core backend of [Salon Tracker](https://github.com/AtakanUk/salon-tracker), a tablet-first app built for a real hair salon in Germany: employees tap **Start** when a customer sits down and pick the services when they finish; the owner sees revenue, employee performance and the service mix.

The original backend is Node.js. This repository is a complete port to modern .NET, written against the same API contract: the React frontend runs on it **unchanged** (it is pulled in as a git submodule), and backups move between the two versions in both directions - one of the tests restores a backup written by the Node version and signs in with a password it hashed.

**Stack:** .NET 10 · ASP.NET Core Minimal APIs · EF Core 10 + Npgsql · PostgreSQL 17 · FluentValidation · ClosedXML · MailKit · xUnit v3 · Testcontainers · Docker

![Owner dashboard, served by this backend](docs/screenshots/dashboard.png)

| At the chair | Finishing a customer | Records |
| --- | --- | --- |
| ![Work screen](docs/screenshots/work-active.png) | ![Service picker](docs/screenshots/work-finish.png) | ![Records](docs/screenshots/records.png) |

## What it does

- **Tablet flow:** start a customer, finish with the services done (plus a custom service with a typed-in amount), correct the last record for 30 minutes.
- **Owner panel:** dashboard by day/week/month in the salon's time zone, price list, employee accounts with soft delete and restore, records with manual entry and Excel export.
- **Operations:** nightly backup (`pg_dump` + Excel + JSON) mailed and kept with a retention rule, restore that only adds missing records, retention cleanup that needs a fresh backup, a health banner, an SSH admin menu and a watchdog.

The full feature list and the product decisions are in the [Node repository](https://github.com/AtakanUk/salon-tracker#features).

## How it maps

| Concern | Node version | This version |
| --- | --- | --- |
| HTTP | Fastify routes | Minimal API endpoint groups, one class per feature |
| Validation | Zod | FluentValidation through an endpoint filter |
| Data | Prisma | EF Core 10 + Npgsql, snake_case, migrations on start |
| One open customer per employee | partial unique index written into the SQL migration | `HasIndex(...).IsUnique().HasFilter("status = 'ACTIVE'")` |
| Auth | `@fastify/jwt` cookie | JWT bearer reading an httpOnly cookie, role re-read from the database per request |
| Rate limiting | `@fastify/rate-limit`, `trustProxy: 1` | built-in `RateLimiter`, `ForwardedHeaders` with `ForwardLimit = 1` |
| Errors | `setErrorHandler` | `IExceptionHandler` → `{ "error": code }` |
| Nightly backup | `setTimeout` loop | `BackgroundService` on a `TimeProvider` |
| Excel | ExcelJS | ClosedXML |
| Server tools | separate scripts | subcommands of the app: `seed`, `backup`, `reset-password`, `import-json`, `test-mail` |
| Tests | Vitest + `inject` | xUnit v3 + `WebApplicationFactory` + Testcontainers |

## Engineering highlights

- **Contract-first port.** Response records reproduce the exact JSON the web app reads, including which relations are left out and how enums are spelled; partial updates distinguish `null` from "not sent" where it matters.
- **Business rules in one place.** `PricedLine` is the only code that decides a line's price: listed services from the database, only the custom service from the request. Corrections keep existing snapshots.
- **The database enforces what must never happen.** A filtered unique index guarantees one open customer per employee; the API catches the violation and answers `409`.
- **Access that cannot outlive a change.** The JWT says who you are, the database says what you may do - on every request. A test demotes an owner and watches the next request fail.
- **Rate limiting behind a proxy, tested.** A test sends 21 logins with forged `X-Forwarded-For` values through the real middleware and gets `429` on the 21st.
- **Time as a dependency.** Everything reads `TimeProvider`; the 30-minute correction window is tested by moving the clock, not by waiting. Daylight saving days are covered by unit tests.
- **Restores that cannot do damage.** `INSERT ... ON CONFLICT DO NOTHING` batched with `NpgsqlBatch` in one transaction, identity sequences moved past the restored ids, compatible with the Node version's files.
- **Strict build.** Nullable reference types, warnings as errors, style rules enforced in the build, central package management with transitive pinning.

More in [docs/architecture.md](docs/architecture.md).

## Getting started

### Docker (everything in one go)

```bash
git clone --recurse-submodules https://github.com/AtakanUk/salon-tracker-dotnet.git
cd salon-tracker-dotnet
cp .env.example .env                  # set DB_PASSWORD and JWT_SECRET
docker compose up -d --build
docker compose exec app dotnet SalonTracker.Api.dll seed --demo   # prints the passwords once
```

Open <http://localhost:3001> and sign in as `admin` (owner) or `ali` (employee). For a real server with Tailscale, mail, the watchdog and the optional public mode, see [docs/deployment.md](docs/deployment.md).

### Local development

Requires the .NET 10 SDK, a PostgreSQL 17 (for example `docker run -d -p 5432:5432 -e POSTGRES_PASSWORD=postgres postgres:17-alpine`) and, for the web app, Node.js 24.

```bash
export DATABASE_URL=postgresql://postgres:postgres@localhost:5432/salon JWT_SECRET=dev-secret
dotnet run --project src/SalonTracker.Api -- seed --demo   # migrations + demo data, prints passwords
dotnet run --project src/SalonTracker.Api                  # API on :3001

# another terminal: the web app on :5173, proxying /api to :3001
cd frontend && npm ci -w web && npm run dev -w web
```

In development the API also serves its OpenAPI document at `/openapi/v1.json`.

## Tests

```bash
dotnet test
```

73 tests run against a real PostgreSQL 17. By default Testcontainers starts one (Docker needed); set `TEST_DATABASE_URL` to use an existing server instead - its database name has to end in `_test`, because every test truncates the tables.

They cover price snapshots and corrections, the custom service, the one-open-customer index, the correction window, role checks and role changes, rate limiting behind a proxy, soft delete and restore, statistics across midnight and daylight saving changes, backup retention, the Excel export, the JSON round trip and a backup written by the Node version. CI builds and tests on every push, then builds the Docker image, starts the compose stack, signs in through the API and takes a backup with the real `pg_dump`.

## Project structure

```text
src/SalonTracker.Api/     the API: Features/, Data/, Auth/, Backup/, Infrastructure/, Cli/
tests/SalonTracker.Api.Tests/
frontend/                 the React app (git submodule of salon-tracker)
ops/                      admin menu, watchdog, server setup, Caddyfile, snapshot scripts
docs/                     architecture, deployment, screenshots
```

## License

[MIT](LICENSE)
