# AI Usage

This service was built with Claude (Anthropic) doing the primary implementation from the
task brief, directed and reviewed turn-by-turn rather than accepted wholesale. This file
documents that process honestly, including where the AI's first pass was wrong.

## Tools used

- **Claude** (Sonnet, via claude.ai) — end-to-end implementation: solution structure,
  EF Core model, the transfer/idempotency/daily-limit logic in `WalletService`, JWT
  wiring, Docker Compose setup, and the Testcontainers-based test suite.

## Representative prompts

1. **"Implement the transfer endpoint so it's concurrency-safe against the hard
   constraint that balance must never go negative under any interleaving of concurrent
   requests."**
   First response used EF Core's default optimistic concurrency (a `Version`/rowversion
   column) plus a check-then-act pattern: read balance, check `>= amount`, decrement,
   `SaveChanges()`, catch `DbUpdateConcurrencyException` and retry. This is a reasonable
   *general* EF pattern but wrong for this specific requirement — see "Caught and fixed"
   below.

2. **"Two wallets need to be locked for a transfer — what happens if two transfers in
   opposite directions between the same pair of wallets run at the same time?"**
   This prompt was deliberately adversarial to probe a specific known class of bug.
   Claude correctly identified the classic lock-ordering deadlock (A→B locks A then waits
   for B; concurrently B→A locks B then waits for A; both wait forever) and fixed it by
   always acquiring the two `SELECT ... FOR UPDATE` locks in ascending wallet-`Guid`
   order, regardless of which wallet is logically the source. This is implemented in
   `WalletService.TransferAsync` and is exactly the kind of thing that's easy to miss
   until you ask for it explicitly, because the happy-path (one direction only) test
   would never surface it.

3. **"Write a test that would actually fail if someone removed the row lock."**
   The first version of `ConcurrentTransfers_...` only asserted `finalBalance >= 0`. That
   passes even for a badly broken implementation that, say, allows a small amount of
   over-spend due to a race, as long as it happens to clamp at zero somewhere, or one that
   just serializes requests so slowly nothing ever overlaps. Tightened per the follow-up
   to assert the *exact* expected count of successful transfers (`startingBalance /
   amountPerTransfer`) and the *exact* final balance — a test that would only pass if the
   locking is actually doing its job under real contention, using a real Postgres
   container (Testcontainers) rather than EF's InMemory provider, which silently ignores
   transactions and row locks entirely and would make this whole class of bug invisible.

## Where the AI's output was wrong, and how it was caught

**The concurrency bug:** the first draft (prompt 1 above) used EF Core optimistic
concurrency — a `uint Version` property on `Wallet` mapped with `.IsRowVersion()` — as the
*sole* concurrency-safety mechanism, combined with a plain LINQ read/update/`SaveChanges`.
Two problems, one subtle and one that would have caused an outright runtime failure:

1. **Correctness-adjacent but sub-optimal:** optimistic concurrency here means the
   *loser* of a race gets an exception and must retry the whole operation, rather than
   simply waiting its turn. Under the kind of load the brief's test explicitly asks for
   (many concurrent requests against one wallet), that turns into a retry storm rather
   than a queue — worse latency behaviour for no correctness benefit over pessimistic
   locking for this access pattern (few hot rows, read-then-write, must-not-race).

2. **The actual bug:** the plan combined `.IsRowVersion()` with `FromSqlInterpolated("SELECT
   * FROM \"Wallets\" WHERE \"Id\" = {id} FOR UPDATE")` to get row-level locking *and*
   optimistic concurrency at once. `IsRowVersion()` without an explicit Npgsql column
   mapping (e.g. onto the `xmin` system column) tries to materialize a `Version` column
   that doesn't reliably line up with `SELECT *` column ordering/typing the way EF expects
   for raw-SQL entity materialization — this is a known Npgsql/EF Core rowversion gotcha,
   not a hypothetical one. It would have either thrown at startup/first query or, worse,
   silently mapped the wrong column. For a wallet balance, "silently maps the wrong
   column" is not a bug you want to discover in production.

   **Fix:** dropped optimistic concurrency entirely. Pessimistic locking via
   `SELECT ... FOR UPDATE` inside an explicit transaction is sufficient on its own here —
   it's a stronger guarantee than optimistic concurrency for this access pattern, not a
   weaker one, so the rowversion column was pure incidental complexity that introduced a
   real bug for no safety benefit. Simpler and more correct. This is the kind of thing
   that's easy for an AI (or a human moving fast) to reach for by default because it's a
   commonly-recommended EF Core pattern in general — the judgment call was recognizing it
   was the wrong tool for *this* specific job and that layering it on top of explicit row
   locking was actively harmful rather than defense-in-depth.

**A precision issue that was avoided rather than caught after the fact:** every draft
from the first prompt onward used `long` kobo throughout, never `decimal` or `double` for
money. This was specified in the initial prompt as a hard constraint, so it's less a case
of "AI got it wrong and was corrected" than "the constraint was stated up front and
verified in review" — worth naming here because it's exactly the kind of thing that goes
wrong silently in a financial system if you don't say it explicitly (an AI asked generically
to "build a wallet balance" will often default to `decimal` for money, which is *better*
than `double` but still not what an integer-minor-unit system like this wants end-to-end).
