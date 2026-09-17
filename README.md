# NovaWallet Ledger Service

A simplified wallet ledger for FirstBank NovaPay's NovaWallet module, built for the
"NovaWallet Ledger Service" take-home task. Treats money the way the brief asks: integer
kobo everywhere, concurrency-safe transfers, idempotent replay, a hard daily outbound
limit, and an append-only audit trail.

## Contents

- [Quick start](#quick-start)
- [Architecture](#architecture)
- [How concurrency safety actually works](#how-concurrency-safety-actually-works)
- [Idempotency](#idempotency)
- [Daily limit](#daily-limit)
- [Auth](#auth)
- [API walkthrough](#api-walkthrough)
- [Testing](#testing)
- [Trade-offs & assumptions](#trade-offs--assumptions)
- [What's deliberately out of scope](#whats-deliberately-out-of-scope)

## Quick start

Requires Docker Desktop. Nothing else needs to be installed on the host.

```bash
docker compose up --build
```

This starts Postgres and the API. On first boot the API waits for Postgres to become
healthy, then creates the schema. Once it logs `Database schema ready.`:

- Swagger UI: http://localhost:8080/swagger
- API base URL: http://localhost:8080

To exercise it end to end:

```bash
# 1. Mint a dev bearer token (stands in for a real identity provider — see Auth below)
TOKEN=$(curl -s -X POST http://localhost:8080/auth/dev-token \
  -H "Content-Type: application/json" \
  -d '{"customerId":"cust-001"}' | python3 -c "import sys,json;print(json.load(sys.stdin)['accessToken'])")

# 2. Create two wallets
WALLET_A=$(curl -s -X POST http://localhost:8080/wallets \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"customerId":"cust-001"}' | python3 -c "import sys,json;print(json.load(sys.stdin)['id'])")

WALLET_B=$(curl -s -X POST http://localhost:8080/wallets \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"customerId":"cust-002"}' | python3 -c "import sys,json;print(json.load(sys.stdin)['id'])")

# 3. Credit wallet A (simulate an inbound NIP transfer of ₦1,000)
curl -s -X POST http://localhost:8080/wallets/$WALLET_A/credit \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"amountKobo": 100000, "reference": "seed"}'

# 4. Transfer ₦250 from A to B (Idempotency-Key is required)
curl -s -X POST http://localhost:8080/wallets/$WALLET_A/transfers \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -H "Idempotency-Key: demo-key-1" \
  -d "{\"toWalletId\": \"$WALLET_B\", \"amountKobo\": 25000, \"reference\": \"rent\"}"

# 5. Statement for A
curl -s http://localhost:8080/wallets/$WALLET_A/statement -H "Authorization: Bearer $TOKEN"
```

### Working on it in VS Code

```bash
git clone <your-fork-url>
cd NovaWalletLedger
code .
```

You only need `dotnet compose up` to actually *run* the service. If you want to run the
API directly from VS Code against the containerised Postgres (for debugging with
breakpoints):

```bash
docker compose up db -d          # just the database
cd src/NovaWalletLedger.Api
dotnet user-secrets set "ConnectionStrings:Default" "Host=localhost;Port=5433;Database=novawallet;Username=novawallet;Password=novawallet_dev_pw"
dotnet run
```

(Port 5433 because `docker-compose.yml` maps the container's 5432 to host 5433 to avoid
clashing with a local Postgres install.)

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

Errors are RFC 7807 Problem Details (`application/problem+json`) with `status`, `title`,
`detail`, and a `traceId` extension for correlating with logs.

## Testing

```bash
docker compose up db -d   # tests need a running Docker daemon (Testcontainers spins up its own Postgres)
cd tests/NovaWalletLedger.Tests
dotnet test
```

Tests boot the real API in-process (`WebApplicationFactory<Program>`) against a real,
throwaway Postgres container per test run (via Testcontainers) — deliberately **not**
EF Core's InMemory provider, because InMemory doesn't honour transactions or row locks and
would let every concurrency bug this suite exists to catch pass silently.

Covered: concurrent transfers past available balance, concurrent credits, idempotent
replay, idempotency-key payload mismatch, missing idempotency key, daily limit
enforcement.

## Trade-offs & assumptions

- **`Database.EnsureCreated()` instead of EF migrations.** For a task with a 48–72 hour
  window, hand-authoring migration snapshot files without a local `dotnet` toolchain to
  generate and verify them against felt riskier than documenting the trade-off. In a real
  codebase this would be `dotnet ef migrations add InitialCreate` committed to source
  control, applied via `dbContext.Database.Migrate()` on startup instead.
- **Pessimistic locking (`FOR UPDATE`) over optimistic concurrency / retry loops.** Simpler
  to reason about correctness for a small number of hot rows (wallets), at the cost of
  transactions queuing rather than racing-and-retrying under very high contention on a
  single wallet. For NovaWallet's actual traffic shape (many wallets, low contention per
  wallet) this is the right trade-off; a system with a few extremely hot wallets (e.g. a
  merchant settlement account) might prefer optimistic concurrency with backoff instead.
- **WAT modelled as a fixed UTC+1 offset**, not an IANA timezone id, so the "midnight WAT"
  reset doesn't depend on the OS's tzdata being installed/current inside the container.
  Correct for Nigeria (no DST); would need revisiting for a market that observes DST.
- **Idempotency-Key required only on transfers**, not credit — the brief specifies it for
  the transfer endpoint; credit is modelled as one-directional and less prone to accidental
  client-side retries mattering as much, though a production system would likely want it
  there too.
- **Rate limiting is a per-user/IP fixed window**, not sliding window or token bucket —
  simplest option that still demonstrates the middleware; tuned generously (200 req/10s)
  so it doesn't interfere with legitimate concurrent load or the test suite.

## What's deliberately out of scope

Per the brief's own note that this is "intentionally more than can be gold-plated" —
prioritized correctness and the concurrency/idempotency hard constraints over these:

- **Outbox pattern / `TransferCompleted` event publishing** — not implemented. Would be
  the next thing added for a real event-driven NovaPay, so other modules (NovaSave
  round-ups, NovaBiz settlement) can react to transfers without polling.
- **KYC tiering / BVN-NIN checks** — out of scope per the brief; the service assumes
  wallets are already provisioned for a KYC'd customer.
- **Refresh tokens / token revocation** — the dev issuer mints short-lived (1h) tokens only.
