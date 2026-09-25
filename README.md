# Order Processing System

Backend for an e-commerce order processing system: customers place orders, track them, and cancel
them; operations staff move them through fulfilment; and a background job promotes pending orders
every five minutes.

Built with .NET 10, ASP.NET Core, EF Core and SQLite. **No Docker, no database server, no cloud
resources** — clone it and run it.

---

## Quick start

```bash
dotnet run --project src/OrderProcessing.Api
```

Then open **<http://localhost:5000/swagger>**.

The database is created, migrated and seeded on first run. No configuration, no containers, no
connection string to set.

```bash
dotnet test        # 269 tests, no external dependencies
```

To reset everything, delete `src/OrderProcessing.Api/orders.db` and restart.

### Getting a token in 10 seconds

Endpoints require a JWT. Issue one from the development token endpoint:

```bash
curl -X POST http://localhost:5000/api/v1/dev/token \
  -H "Content-Type: application/json" \
  -d '{"userId":"11111111-1111-1111-1111-111111111111","role":"Customer","email":"alice@example.com"}'
```

Paste the token into Swagger's **Authorize** box, or use [`requests.http`](./requests.http), which
walks the entire feature set end to end — including the security cases.

**Seeded identities**

| Who | Id | Role |
| --- | --- | --- |
| Alice | `11111111-1111-1111-1111-111111111111` | Customer |
| Bob | `22222222-2222-2222-2222-222222222222` | Customer |
| Admin | `33333333-3333-3333-3333-333333333333` | Admin |

Five products are seeded, one deliberately inactive so the rejection path can be exercised by hand.

---

## API

Base path `/api/v1`. All endpoints require authentication except health checks and the dev token.

| Method | Path | Who | Purpose |
| --- | --- | --- | --- |
| `POST` | `/orders` | Customer | Place an order (supports `Idempotency-Key`) |
| `GET` | `/orders/{id}` | Owner / Admin | Retrieve an order with its audit trail |
| `GET` | `/orders` | Owner / Admin | List with filtering, sorting, pagination |
| `PATCH` | `/orders/{id}/status` | **Admin** | Transition status |
| `POST` | `/orders/{id}/cancel` | Owner / Admin | Cancel |
| `GET` | `/products` | Any | Browse the catalogue |
| `GET` | `/health/live`, `/health/ready` | Anonymous | Health probes |
| `POST` | `/dev/token` | Anonymous | **Development only** |

Errors use RFC 7807 `ProblemDetails` with a correlation id. A rejected transition also returns the
transitions that *were* available, so the error is actionable:

```json
{
  "type": "https://orderprocessing/errors/invalid-status-transition",
  "title": "Invalid status transition",
  "status": 409,
  "detail": "Cannot transition order from Pending to Delivered as Admin; permitted transitions are: Processing, Cancelled.",
  "permittedTransitions": ["PROCESSING", "CANCELLED"],
  "correlationId": "0HNOR1BMF04TF"
}
```

---

## Design decisions

The brief is five bullet points. Most of the engineering is in the decisions it does not mention.
Full reasoning is in [docs/SPECIFICATION.md](./docs/SPECIFICATION.md); the highlights:

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

Ownership is enforced by **scoping the query**, not by loading a row and comparing ids afterwards:

```csharp
private static IQueryable<Order> ScopeToCaller(IQueryable<Order> source, Actor caller) =>
    caller.IsAdmin ? source : source.Where(order => order.CustomerId == caller.UserId);
```

Fetch-then-compare works until someone adds an endpoint and forgets the check. Query-scoping is
fail-closed: omitting it returns *nothing*, not *everything*.

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

### The background job claims work atomically

The brief's one line — *"update PENDING orders to PROCESSING every 5 minutes"* — hides the real
questions: what happens with two instances, is the backlog loaded wholesale, does one bad order
roll back the batch, and how do you test a five-minute timer without waiting five minutes?

Claiming is a single atomic statement:

```sql
UPDATE orders
SET "Status" = 'Processing', "UpdatedAt" = $now, "Version" = "Version" + 1
WHERE "Id" IN (
    SELECT "Id" FROM orders WHERE "Status" = 'Pending' ORDER BY "CreatedAt" LIMIT $batchSize
)
AND "Status" = 'Pending'
RETURNING "Id";
```

The trailing `AND "Status" = 'Pending'` is load-bearing: it re-asserts the expected state at
execution time, so an order cancelled between selection and update is not promoted. `RETURNING`
reports exactly which rows changed, so history is written only for orders genuinely transitioned.

This sits behind an `IPendingOrderClaimer` port. PostgreSQL would use
`SELECT … FOR UPDATE SKIP LOCKED`; the scheduler, the domain and the tests are unchanged either way.

The job takes its clock from `TimeProvider`, so a five-minute schedule is verified in microseconds
with no `Thread.Sleep` and no flakiness.

### Concurrency is handled explicitly

An application-managed `Version` column is an EF Core concurrency token. The race that matters:
**an admin cancels while the scheduler promotes.** One transaction wins; the other gets
`DbUpdateConcurrencyException` and re-evaluates against the new state. Deterministic, not
timing-dependent — and covered by a test that runs both operations concurrently.

---

## Testing

**269 tests**, all against a real SQLite database.

| Suite | Count | Scope |
| --- | --- | --- |
| Domain | 217 | State machine, `Money`, aggregate invariants, clock |
| Integration | 52 | Full HTTP stack, security, scheduler, concurrency |

Not the EF Core InMemory provider: it enforces no unique constraints, foreign keys or check
constraints, so the idempotency and integrity tests would pass there without exercising anything. A
test suite that cannot fail is worse than no suite.

### Tests written to actually bite

The transition matrix is verified across **all 75** `(actor × from × to)` combinations, checked
against an expectation declared longhand in the test file. Deriving it from the production table
would be tautological — it would pass for any implementation, including one permitting everything.

Both critical invariants were **mutation-tested**:

- Granting customers the admin cancellation window → **4 domain tests failed**
- Breaking the claim statement's atomicity → **5 integration tests failed**

Both passed again on revert. A suite that has never been seen to fail is an assumption, not
evidence.

The mutation run also exposed a flaw in the tests themselves: an unbounded claim loop meant a
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

---

## Architecture

```
src/
  OrderProcessing.Domain/          entities, Money, state machine   (no dependencies)
  OrderProcessing.Application/     use cases, DTOs, ports
  OrderProcessing.Infrastructure/  EF Core, repositories, auth, scheduler
  OrderProcessing.Api/             controllers, middleware, composition root
tests/
  OrderProcessing.Domain.Tests/        fast, no I/O
  OrderProcessing.Integration.Tests/   full stack, real SQLite
```

Dependencies point inward: `Api → Infrastructure → Application → Domain`. The domain references
nothing.

The layering is load-bearing rather than decorative here: the two database-specific pieces — the
claim strategy and the concurrency token — are exactly the parts that differ between SQLite and
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
| [docs/SPECIFICATION.md](./docs/SPECIFICATION.md) | Full design spec: ~50 numbered requirements, API contract, security model, NFRs, decision log |
| [docs/AI-USAGE.md](./docs/AI-USAGE.md) | Required AI-usage log — what AI was used for, what it got wrong, how it was corrected |
| [requests.http](./requests.http) | Executable walkthrough of every feature, including security cases |

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
