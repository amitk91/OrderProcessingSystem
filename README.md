# Order Processing System

Backend for an e-commerce order processing system: customers place orders, track them, and cancel
them; operations staff move them through fulfilment; and a background job promotes pending orders
every five minutes.

Built with .NET 10, ASP.NET Core, EF Core and SQLite. **No Docker, no database server, no cloud
resources** — clone it and run it.

> **Looking for endpoint details?** The full HTTP contract — request and response schemas, query
> parameters, status codes and error format — is in **[docs/API.md](./docs/API.md)**.

---

## Quick start

**Requires the .NET 10 SDK** ([download](https://dotnet.microsoft.com/download/dotnet/10.0)) — and
nothing else. No Docker, no database server, no connection string.

```bash
dotnet run --project src/OrderProcessing.Api
```

Then open **<http://localhost:5000/swagger>**.

The database is created, migrated and seeded on first run.

```bash
dotnet test        # 324 tests, no external dependencies
```

To reset everything, delete `src/OrderProcessing.Api/orders.db` and restart.

### Getting a token in 10 seconds

Endpoints require a JWT. Issue one from the development token endpoint:

```bash
curl -X POST http://localhost:5000/api/v1/dev/token \
  -H "Content-Type: application/json" \
  -d '{"userId":"11111111-1111-1111-1111-111111111111","role":"Customer","email":"alice@example.com"}'
```

Paste it into Swagger's **Authorize** box, or use [`requests.http`](./requests.http), which walks
the entire feature set end to end — including the security cases.

**Seeded identities**

| Who | Id | Role |
| --- | --- | --- |
| Alice | `11111111-1111-1111-1111-111111111111` | Customer |
| Bob | `22222222-2222-2222-2222-222222222222` | Customer |
| Admin | `33333333-3333-3333-3333-333333333333` | Admin |

Five products are seeded, one deliberately inactive so the rejection path can be exercised by hand.

---

## What the system does

### Placing an order

A customer submits a list of products and quantities. Prices are **resolved from the catalogue**,
never taken from the request, so a caller cannot influence what it is charged. Line totals and the
order total are computed server-side. Duplicate products in one request are merged into a single
line with the quantities summed.

Every order starts in `PENDING` and receives a human-readable number such as `ORD-2026-000042`.

Creation is **idempotent** when the client supplies an `Idempotency-Key`: a double-tapped checkout
button or a retry after a network timeout returns the original order rather than creating a second
one.

### Tracking an order

An order can be fetched by id, or listed with filtering by status, sorting by creation date or
total, and pagination. Every order carries a complete **audit trail** recording each status change,
who made it, when, and why.

### Moving an order through fulfilment

`PENDING → PROCESSING → SHIPPED → DELIVERED`, with `CANCELLED` reachable before dispatch. Every
transition is governed by the matrix below.

### Cancelling an order

Customers may cancel while an order is still `PENDING`. Administrators may also cancel while it is
`PROCESSING`, and must give a reason. Cancellation records who did it, when, and why.

### Automatic promotion

A background job runs every five minutes and promotes pending orders to processing. Promotions are
attributed to `SYSTEM` in the audit trail. Orders cancelled before a run are never promoted.

---

## Behavioural rules

The brief is five bullet points. Most of the engineering is in the decisions it does not mention.
Full reasoning is in [docs/SPECIFICATION.md](./docs/SPECIFICATION.md); the ones that shape
behaviour:

### The state machine is role-aware

Transitions are governed by **both** the current state and the acting role — a
`(actor, from) → to` matrix, not a simple `from → to` table:

| From → To | `PROCESSING` | `SHIPPED` | `DELIVERED` | `CANCELLED` |
| --- | --- | --- | --- | --- |
| **`PENDING`** | Admin, System | ✗ | ✗ | Customer, Admin |
| **`PROCESSING`** | ✗ | Admin | ✗ | **Admin only** |
| **`SHIPPED`** | ✗ | ✗ | Admin | ✗ |
| **`DELIVERED`** | ✗ | ✗ | ✗ | ✗ terminal |
| **`CANCELLED`** | ✗ | ✗ | ✗ | ✗ terminal |

The brief grants cancellation only to customers in `PENDING`. That is not how operations works:
staff need to cancel in-flight orders for fraud review, late payment failures or support calls. So
administrators may also cancel from `PROCESSING`, **with a mandatory reason** — an override is only
defensible if it is attributable.

Cancellation stops at `PROCESSING` on purpose. Once goods have shipped, reversal is a returns flow
with its own restocking and refund semantics; calling that a "cancel" would misrepresent it.

The matrix is enforced in exactly one place — `Order.TransitionTo` — which the controllers *and*
the background job both route through. There is no second copy to drift.

### Security is three distinct layers

Conflating these is the most common source of access-control bugs:

1. **Authentication** — JWT bearer; identity comes from the token's `sub` claim and nowhere else.
2. **Role authorization** — *may this kind of actor do this?* A customer calling `PATCH /status`
   gets `403`.
3. **Ownership** — *may this actor touch this row?* Passing (2) but failing (3) is the
   IDOR / Broken Object-Level Authorization defect, top of the OWASP API Security Top 10.

Ownership is enforced by **scoping the query**, not by loading a row and comparing ids afterwards.
The caller's visibility is resolved once, into a value:

```csharp
public static OrderScope For(Actor caller) =>
    caller.IsAdmin ? All : ForCustomer(caller.UserId);
```

…and every read on the repository port takes that scope as a **required parameter**:

```csharp
Task<Order?> FindAsync(Guid orderId, OrderScope scope, CancellationToken ct);
```

There is no overload that omits it, so a new call site cannot silently query across customers —
**the compiler enforces it, not code review.** That is what makes the design fail-closed; a helper
you are merely *expected* to remember to call is not.

**Another customer's order returns `404`, not `403`.** A `403` confirms the order exists, which
lets an attacker enumerate ids. `404` reveals nothing. `403` is still used where the *operation* is
forbidden regardless of ownership, because that leaks nothing.

### Money is stored as integer minor units

`$49.99` is persisted as `4999`, behind a `Money` value object.

This is not gold-plating. SQLite has no `decimal` type, and EF Core maps `decimal` to TEXT — which
sorts **lexicographically**, so `"9.99"` sorts after `"100.00"`. Sorting orders by total would have
returned wrong results *and* wrong pagination, and EF Core reports it as a warning rather than an
error, so it would very plausibly have shipped unnoticed. Mapping to REAL instead would have traded
a sorting bug for floating-point error in money.

Integers sort correctly, compare correctly and aggregate exactly. It is also what Stripe and Adyen
do, so the constraint forced a good decision earlier rather than imposing a workaround.

### Order items hold price snapshots

`ProductName` and `UnitPrice` are copied onto the order at creation, not joined from the live
catalogue. If a price changes later, historical orders still show what the customer actually agreed
to pay. Recomputing an old total from current prices would be a correctness and compliance defect.

**An order's currency is derived from its lines, not supplied.** `Order.Create` reads the distinct
currencies of the requested items and rejects the order unless there is exactly one. An earlier
revision took the currency from whichever product happened to be listed first, which made the
outcome depend on request ordering and turned a mismatch into a bare `InvalidOperationException` —
a `500` for a request whose products and quantities were all perfectly valid.

Mixing currencies now returns `422` naming the currencies involved. Multi-currency is out of scope,
and this is the explicit rejection that scope decision implies rather than an implicit,
order-dependent one.

### The background job claims work, then promotes through the aggregate

The brief's one line — *"update PENDING orders to PROCESSING every 5 minutes"* — hides the real
questions: what happens with two instances, is the backlog loaded wholesale, does one bad order
roll back the batch, and how do you test a five-minute timer without waiting five minutes?

Each batch runs in **one transaction**, in three steps:

```csharp
await using var transaction = await orders.BeginTransactionAsync(ct);

var ids     = await claimer.ClaimPendingOrdersAsync(batchSize, ct);  // 1. lock
var claimed = await orders.LoadForPromotionAsync(ids, ct);

foreach (var order in claimed)
    order.PromoteToProcessing(now);                                  // 2. transition

await orders.SaveChangesAsync(ct);
await transaction.CommitAsync(ct);                                   // 3. commit together
```

**A claim is a lock, not a transition.** That separation is the point. Claiming answers *who gets
to work on this order*, which is a concurrency concern and provider-specific. Promoting answers
*what state it moves to*, which is a business rule and belongs to the aggregate.

Under SQLite the lock is taken by stamping a lease, since there is no `FOR UPDATE SKIP LOCKED`:

```sql
UPDATE orders SET "PromotionLease" = $lease
WHERE "Id" IN (
    SELECT "Id" FROM orders
    WHERE "Status" = 'Pending' AND "PromotionLease" IS NULL
    ORDER BY "CreatedAt" LIMIT $batchSize)
AND "Status" = 'Pending' AND "PromotionLease" IS NULL
RETURNING "Id";
```

The lease is an EF Core **shadow property** — it exists in the database but not on the `Order`
aggregate, so a scheduling mechanism never leaks into the domain. It is cleared in the same
transaction that promotes the order, so it cannot outlive the work it guards and there are no
stale leases to reap. PostgreSQL would use `SELECT … FOR UPDATE SKIP LOCKED` instead and need no
lease at all; both sit behind `IPendingOrderClaimer`.

**Why not do it in one statement?** An earlier version did:
`UPDATE … SET Status = 'Processing' … RETURNING Id`. It was atomic and behaved correctly, but it
had two flaws worth naming:

1. It **restated the transition rule in SQL**, so the matrix was no longer enforced in one place.
2. It committed the status change *independently of the audit entry*, so an interrupted run could
   leave an order promoted with no history — violating FR-6.5.

The job takes its clock from `TimeProvider`, so a five-minute schedule is verified in microseconds
with no `Thread.Sleep` and no flakiness.

### Concurrency is handled explicitly

An application-managed `Version` column is an EF Core concurrency token. The race that matters:
**an admin cancels while the scheduler promotes.** One transaction wins; the other gets
`ConcurrencyConflictException` and re-evaluates against the new state. Deterministic, not
timing-dependent — and covered by a test that runs both operations concurrently.

**Uniqueness is enforced by the database, not by a preceding read.** Order creation has two values
that must be unique — the idempotency key and the order number — and both are allocated by
read-then-write, which cannot be made race-free on its own. Two requests can both miss the read
before either writes.

So the unique index is treated as the enforcement, and its violation as a *signal*:

| Collision | Response |
| --- | --- |
| Same idempotency key | Re-read the key and return the order the winner created — `200` |
| Same order number | Transient; allocate a fresh number and retry, bounded at five attempts |

Twelve concurrent requests sharing a key produce **one `201`, eleven `200`s, and one order**.
Twelve concurrent requests without a key produce **twelve orders with twelve distinct numbers**.
Both are tested.

This is worth calling out because read-then-write is the default shape of an idempotency check and
it passes every sequential test. The failure only appears under real concurrency — exactly the
situation the feature exists to survive.

---

## Testing

**324 tests**. The domain and application suites need no I/O at all; the integration suite runs against a real SQLite database.

| Suite | Count | Scope |
| --- | --- | --- |
| Domain | 225 | State machine, `Money`, aggregate invariants, clock, **architecture rules** |
| Application | 21 | Use cases against substituted ports — **no database, no host** |
| Integration | 78 | Full HTTP stack, security, scheduler, creation races, promotion integrity, currency, error mapping |

Not the EF Core InMemory provider: it enforces no unique constraints, foreign keys or check
constraints, so the idempotency and integrity tests would pass there without exercising anything. A
test suite that cannot fail is worse than no suite.

### Tests written to actually bite

The transition matrix is verified across **all 75** `(actor × from × to)` combinations, checked
against an expectation declared longhand in the test file. Deriving it from the production table
would be tautological — it would pass for any implementation, including one permitting everything.

Critical invariants are **mutation-tested**, and the audit is reproducible rather than asserted:

```bash
pwsh tools/mutation-audit.ps1
```

It breaks each invariant in turn, runs the suite, restores the source, and prints what failed.
Most recent run against the current code:

| Invariant broken | Tests failed |
| --- | --- |
| Promotion bypasses the transition matrix | 8 |
| Order currency reverts to "first line wins" | 6 |
| Customers granted the admin cancellation window | 5 |
| Ownership scoping removed (customers see every order) | 3 |
| Claim drops the pending-status filter | 2 |
| Unique-violation translation disabled for order numbers | 2 |
| **Claim drops the lease guard** | **0** — see below |

**The zero is the interesting row, and it is left in deliberately.** Removing
`AND "PromotionLease" IS NULL` from the claim statement breaks no test, because under SQLite it
breaks no behaviour. The lease is cleared in the same transaction that promotes, so no committed
row ever carries one, and the guard is always trivially true.

What actually provides exactly-once claiming here is the pair beneath it: the claim is a *write*,
so SQLite's write lock serialises workers, and the `Status = 'Pending'` filter means a worker that
was blocked finds those rows already promoted. The lease exists to make the claim expressible as a
write *without* changing status — the separation that keeps the transition matrix authoritative —
not to provide mutual exclusion on its own. The guard is defence-in-depth against a future change
that commits between claiming and promoting.

That row was originally reported as *"breaking the claim statement's atomicity → 5 integration
tests failed."* True when written, and false as soon as the claim statement was redesigned: the
statement it described no longer exists. Re-running the audit is what turned a stale number into
an accurate characterisation of how the guarantee is actually achieved.

The mutation runs also exposed a flaw in the tests themselves: an unbounded claim loop meant a
broken implementation *hung* rather than failing. It is now bounded, so a regression produces a red
test in a second instead of a stuck build.

### Cases worth looking at

| Test | Proves |
| --- | --- |
| `A_customer_cannot_read_another_customers_order_and_is_told_it_does_not_exist` | IDOR returns 404, body leaks nothing |
| `A_customer_cannot_widen_their_scope_with_a_customerId_parameter` | Scope cannot be widened by query string |
| `A_cancellation_racing_a_promotion_produces_a_deterministic_outcome` | No lost update under a real race |
| `Concurrent_workers_never_claim_the_same_order_twice` | 4 workers, 24 orders, exactly-once |
| `A_client_supplied_price_is_ignored` | Raw JSON with `unitPrice` cannot tamper with the total |
| `Changing_a_catalogue_price_does_not_alter_an_existing_order` | Snapshot integrity |
| `Orders_sort_by_total_numerically_and_not_as_text` | The decimal-sorting defect stays fixed |
| `Paging_through_results_yields_no_duplicates_and_no_gaps` | Stable sort under pagination |
| `Concurrent_requests_with_the_same_idempotency_key_all_return_the_same_order` | 12 racing requests, one order, no 5xx |
| `Concurrent_creates_without_a_key_produce_distinct_order_numbers` | Order-number collisions retried, not surfaced |
| `Mixing_currencies_in_one_order_is_rejected_with_422_not_500` | Valid products never yield a server error |

---

## Architecture

```
src/
  OrderProcessing.Domain/          entities, Money, state machine   (no dependencies)
  OrderProcessing.Application/     use cases + ports  (no EF Core, no ASP.NET)
  OrderProcessing.Infrastructure/  EF adapters, auth, scheduler host
  OrderProcessing.Api/             controllers, middleware, composition root
tests/
  OrderProcessing.Domain.Tests/        domain + architecture rules, no I/O
  OrderProcessing.Integration.Tests/   full stack, real SQLite
```

Dependencies point inward: `Api → Infrastructure → Application → Domain`. The domain references
nothing.

**Where the code actually lives**, since this is the part that is easy to get wrong:

| Concern | Project | Why |
| --- | --- | --- |
| `Order`, `Money`, transition matrix | Domain | Invariants, no dependencies |
| `OrderService`, `OrderPromotionService` | **Application** | Use cases — orchestration belongs above infrastructure |
| `IOrderRepository`, `IProductCatalog`, `OrderScope` | **Application** | Ports are owned by the layer that *needs* them |
| `EfOrderRepository`, `EfProductCatalog` | Infrastructure | Adapters implementing those ports |
| `OrderPromotionBackgroundService` | Infrastructure | *When* to run is a hosting concern |
| `ClaimsPrincipal → Actor` mapping | Api | `ClaimsPrincipal` is an ASP.NET type |

The promotion job is the clearest illustration: Infrastructure owns the timer and service scope,
Application owns what a run does, and Infrastructure again owns *how* rows are claimed behind
`IPendingOrderClaimer`. No `IQueryable` or `DbContext` crosses into Application.

This is enforced by tests, not just documented — see `ArchitectureTests`.

The layering is load-bearing rather than decorative: the two database-specific pieces — the claim
strategy and the concurrency token — are exactly the parts that differ between SQLite and
PostgreSQL. Confining them behind ports keeps the domain, the use cases and every test identical
across both.

---

## Known limitations

Stated deliberately rather than left to be discovered.

- **Single-node deployment.** The API is written to be stateless and horizontally scalable, but a
  local SQLite file cannot be shared across instances. The claim logic is multi-worker safe and
  tested with concurrent workers, so the guarantee holds the moment the store is swapped — but
  horizontal scale is *designed for*, not *demonstrated*.
- **The PostgreSQL claim strategy is untested here**, because SQLite does not support
  `FOR UPDATE SKIP LOCKED`. `IPendingOrderClaimer` is a port with provider-agnostic contract tests,
  so the same assertions run against PostgreSQL when one is available.
- **Order numbers** derive from the highest existing number for the year. Adequate for a
  single-writer database and backed by a unique index, so a collision fails loudly. A multi-writer
  deployment would use a database sequence.
- **SQLite serialises writes**, so concurrent workers do not parallelise. Correctness holds;
  throughput does not scale. This is the main reason to move to PostgreSQL.
- **The dev token endpoint** issues a token for any user id. It is Development-only and is not an
  authentication system.

### Deliberately out of scope

Inventory reservation, payment processing, carrier integration, returns/refunds, notifications and
multi-currency. Each is a subsystem in its own right; see
[§1.2](./docs/SPECIFICATION.md) for why each was excluded.

---

## Documentation

| Document | Contents |
| --- | --- |
| **[docs/API.md](./docs/API.md)** | **Full HTTP reference** — endpoints, schemas, query parameters, status codes, error codes |
| [docs/SPECIFICATION.md](./docs/SPECIFICATION.md) | Design spec: ~50 numbered requirements, domain model, security model, NFRs, decision log |
| [docs/AI-USAGE.md](./docs/AI-USAGE.md) | Required AI-usage log — what AI was used for, what it got wrong, how it was corrected |
| [requests.http](./requests.http) | Executable walkthrough of every feature, including security cases |
| [tools/mutation-audit.ps1](./tools/mutation-audit.ps1) | Re-runs every mutation claim in this README against the current code |

---

## Configuration

Defaults work with no changes. Override via `appsettings.json` or environment variables:

| Setting | Default | Purpose |
| --- | --- | --- |
| `ConnectionStrings:Default` | `Data Source=orders.db` | Database location |
| `OrderPromotion:Interval` | `00:05:00` | Promotion job interval |
| `OrderPromotion:BatchSize` | `100` | Orders claimed per transaction |
| `OrderPromotion:Enabled` | `true` | Disable the job entirely |
| `Jwt:SigningKey` | dev placeholder | **Must be overridden outside Development** |

To watch the background job without waiting five minutes:

```bash
OrderPromotion__Interval=00:00:05 dotnet run --project src/OrderProcessing.Api
```
