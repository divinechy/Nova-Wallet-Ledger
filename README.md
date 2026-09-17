# NovaWallet Ledger Service

A simplified wallet ledger for FirstBank NovaPay's NovaWallet module, built for the
"NovaWallet Ledger Service" take-home task.

## Contents

- [Quick start](#quick-start)
- [Architecture](#architecture)
- [How concurrency safety actually works](#how-concurrency-safety-actually-works)
- [Idempotency](#idempotency)
- [Daily limit](#daily-limit)
- [Auth](#auth)
- [API walkthrough](#api-walkthrough)

## Quick start

Requires Docker Desktop. Nothing else needs to be installed on the host.

```bash
docker compose up --build
```

This starts Postgres and the API. On first boot the API waits for Postgres to become
healthy, then creates the schema. Once it logs `Database schema ready.`:

- Swagger UI: http://localhost:8080/swagger
- API base URL: http://localhost:8080

## Architecture

```
src/NovaWalletLedger.Api/
  Program.cs                 – composition root: DB, auth, rate limiting, Swagger, middleware pipeline
  Data/AppDbContext.cs       – EF Core model (Wallets, Transactions, AuditLogs, IdempotencyRecords)
  Models/                    – entity classes
  Dtos/                      – request/response contracts (kept separate from entities on purpose)
  Services/WalletService.cs  – all ledger business logic; the only thing that touches the DB
  Services/DevTokenService.cs– mock JWT issuer, dev/test only
  Endpoints/                 – minimal-API route registration, thin, no business logic
  Middleware/                – RFC 7807 exception handling, correlation IDs

tests/NovaWalletLedger.Tests/
  WalletApiFactory.cs        – spins up a real Postgres via Testcontainers + the API in-process
  ConcurrencyTests.cs        – the money-safety tests the brief specifically asks for
  IdempotencyTests.cs
  DailyLimitTests.cs
```

Minimal APIs rather than MVC controllers: this service has five thin endpoints and no view
concerns, so controllers would just be ceremony. `WalletService` holds all the actual
logic and is unit/integration-testable independent of HTTP.

Money is `long` (kobo) end to end — in the entities, the DTOs, and every calculation.
There is no `decimal`/`double` anywhere in the money path.

## How concurrency safety actually works

This is the part the brief cares most about, so here's the actual mechanism, not just the
claim.

Every balance mutation (`CreditAsync`, `TransferAsync`) runs inside a single database
transaction that takes an explicit row lock on the wallet(s) involved:

```sql
SELECT * FROM "Wallets" WHERE "Id" = @id FOR UPDATE
```

`FOR UPDATE` blocks any other transaction from locking (and therefore updating) that same
row until this transaction commits or rolls back. That's what actually prevents two
concurrent transfers from both reading balance = 1000, both deciding "there's enough",
and both deducting — the second transaction simply blocks at the `SELECT ... FOR UPDATE`
until the first one finishes, then re-reads the now-updated balance.

For a transfer, **two** wallets need locking (source and destination). They are always
locked in ascending wallet-`Guid` order, regardless of which one is the source. Without a
fixed lock order, a transfer A→B running concurrently with a transfer B→A could deadlock:
A→B locks A then wants B; B→A locks B then wants A. Locking by a consistent global order
breaks that cycle.

Belt-and-braces: the `Wallets` table also has a Postgres `CHECK ("BalanceKobo" >= 0)`
constraint. The application logic should never let a negative balance reach the database,
but if it ever did (a bug, a bypass, a future contributor removing the lock by mistake),
the write itself is rejected at the database level rather than silently corrupting data.

`ConcurrencyTests.ConcurrentTransfers_NeverAllowNegativeBalance_OrDoubleSpend` fires 40
concurrent transfer requests worth 20x the wallet's actual balance and asserts: every
response is an explicit 201 or 422 (never a crash), exactly as many succeed as the balance
allows, and the final balance is exact — not just non-negative, but exactly
`starting − (successes × amount)`. That's the test that would actually catch a regression
if someone "optimized away" the row lock later.

## Idempotency

The `Idempotency-Key` header is required on `POST /wallets/{id}/transfers`. The key is
claimed by inserting a row into `IdempotencyRecords` **inside the same transaction** as
the balance mutation, so "record that this key was used" and "move the money" are
atomic — one can't happen without the other.

- Same key, same payload, replayed → the original response is returned, no re-processing
  (verified by hashing the request and comparing).
- Same key, **different** payload → `409 Conflict`, per the spec.
- Two requests with the same brand-new key racing each other → the second one hits a
  unique-constraint violation on `IdempotencyRecords.Key` inside its transaction, rolls
  back, re-reads what the first one actually persisted, and returns that (or `409` if a
  third distinct payload used the same key mid-flight).
- Missing header entirely → `400 Bad Request`.

## Daily limit

Enforced server-side per wallet: sum of `TransferOut` amounts since midnight **WAT**
(UTC+1, fixed offset, no DST) must not exceed `DailyOutboundLimitKobo` (defaults to
₦500,000 = 50,000,000 kobo, configurable via `appsettings.json` /
`DailyOutboundLimitKobo` env var). The check happens inside the same locked transaction as
the transfer, so it can't be bypassed by racing requests either.

## Auth

Endpoints require a JWT bearer token. `POST /auth/dev-token` mints one for a given
`customerId` using a symmetric dev signing key — **this stands in for a real identity
provider** (the brief explicitly says a mock issuer is fine; the point being assessed is
the resource-server side: bearer validation middleware and claims handling on the wallet
endpoints, not building an auth server). In a real deployment this endpoint would not
exist; tokens would come from FirstBank's actual IdP and `Jwt:SigningKey` would be a
rotated secret from a vault, not a value in `appsettings.json`.

## API walkthrough

Full interactive docs at `/swagger` once running. Summary:

| Method | Path                                | Notes |
|--------|--------------------------------------|-------|
| POST   | `/auth/dev-token`                    | anonymous; mints a bearer token |
| POST   | `/wallets`                           | create wallet, balance 0 |
| GET    | `/wallets/{id}`                      | balance + currency |
| POST   | `/wallets/{id}/credit`               | deposit (inbound NIP simulation) |
| POST   | `/wallets/{id}/transfers`            | requires `Idempotency-Key` header |
| GET    | `/wallets/{id}/statement`            | paginated, newest first (`?page=&pageSize=`) |
| GET    | `/health/live`, `/health/ready`      | anonymous, for container orchestration |
