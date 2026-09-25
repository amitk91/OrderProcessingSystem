# Order Processing System — Design Specification

**Status:** Finalized (pre-implementation)
**Source brief:** [Func-Req.md](../Func-Req.md)

This document consolidates the functional and non-functional design decisions agreed before
implementation begins. The original brief is deliberately high-level; this specification records
how each requirement was made precise and testable, and — just as importantly — which decisions
were made that the brief did not ask for.

---

## 1. Scope

### 1.1 In scope

- Place an order containing multiple items
- Retrieve a single order by ID
- List orders with filtering, sorting and pagination
- Transition order status through a governed state machine
- Cancel an order, with distinct rules for customers and administrators
- A background job promoting `PENDING` orders to `PROCESSING` every 5 minutes
- Authentication, role-based authorization and per-customer data isolation
- Automated tests covering domain rules, API behaviour, security and concurrency

### 1.2 Explicitly out of scope

These are called out deliberately rather than silently omitted. Each is a realistic part of a
production e-commerce system, excluded here to keep the assignment focused.

| Excluded | Rationale |
| --- | --- |
| Inventory / stock reservation | Requires reservation, expiry and oversell handling — a subsystem in its own right |
| Payment processing | Needs a PSP integration, refunds and reconciliation |
| Shipping / carrier integration | `SHIPPED` is modelled as a state only, with no logistics integration |
| Returns / refunds (RMA) | Directly informs the decision to cap cancellation at `PROCESSING` (§6.3) |
| Notifications (email / SMS) | Would be delivered via the outbox pattern; noted as future work |
| Multi-currency | Single currency assumed; the money type is designed so this can be added |

Separately, this build runs with **no external infrastructure at all** — no Docker, no database
server, no cloud resources. That is a constraint on the deliverable rather than on the design;
see §3.2 for exactly what it does and does not change.

---

## 2. Glossary

| Term | Meaning |
| --- | --- |
| **Order** | A customer's purchase request, containing one or more order items |
| **Order item** | A single product line within an order, holding a price snapshot |
| **Price snapshot** | The unit price copied onto the order item at creation time |
| **Actor** | The authenticated principal performing an operation: `Customer`, `Admin` or `System` |
| **Transition** | A governed change of order status, validated by the state machine |
| **Claim** | The scheduler's act of exclusively locking a batch of orders for processing |

---

## 3. Technology stack

| Concern | Choice | Rationale |
| --- | --- | --- |
| Runtime | .NET 10 (LTS) | Current LTS; built-in `TimeProvider` and `BackgroundService` |
| API | ASP.NET Core Web API | Standard, well-understood, first-class OpenAPI support |
| Persistence | SQLite + EF Core 10 | See §3.1 — zero-install, real relational semantics |
| Migrations | EF Core Migrations | Version-controlled, reviewable schema changes |
| Validation | FluentValidation 12 | Keeps validation rules out of controllers and independently testable |
| Auth | JWT bearer (`Microsoft.AspNetCore.Authentication.JwtBearer`) | Stateless, role claims; symmetric dev signing key |
| API docs | Swashbuckle (Swagger UI) | Interactive contract exploration at `/swagger` |
| Logging | Serilog | Structured JSON logs with correlation IDs (§10.4) |
| Testing | xUnit, Shouldly, NSubstitute, WebApplicationFactory, SQLite | A real relational engine in tests, not a fake provider |
| Package management | Central Package Management | Single source of truth for versions across seven projects |
| External dependencies | **None** | See §3.2 |
| CI | GitHub Actions | Build and test on every push; no service containers required |

### 3.1 Why SQLite

This build runs with **zero external infrastructure** (§3.2), which rules out a server-based
database. Two options remained.

**Why not the EF Core InMemory provider.** It is not a relational database. It does not enforce
unique constraints, foreign keys, check constraints, or real transaction semantics. Tests written
against it pass while the same code fails on any real database — it would silently invalidate
FR-1.12 (unique idempotency key), FR-1.3 (foreign-key integrity) and every concurrency test in
§12.2. Using it would make the test suite actively misleading, which is worse than having fewer
tests.

**Why SQLite.** It is a genuine relational, ACID, transactional engine that runs in-process with
no installation. It enforces constraints and foreign keys, supports transactions and savepoints,
and — for a single-node assignment — gives honest persistence semantics. It ships as a NuGet
package, so `dotnet run` is the entire setup.

**What SQLite costs us, and how each cost is handled:**

| Limitation | Impact | Mitigation |
| --- | --- | --- |
| No `FOR UPDATE SKIP LOCKED` | Scheduler cannot claim rows by locking | Atomic `UPDATE … RETURNING` behind a port (§9.3) |
| No server-generated `rowversion` / `xmin` | No native optimistic concurrency token | Application-managed `Version` column (§10.2) |
| No native `decimal` type | `ORDER BY` on money is unsupported and incorrect | Money stored as integer minor units (§5.4) |
| Single-writer lock | Writes serialize | Acceptable at assignment scale; noted as the reason to move to a server DB |
| No native `uuid` / `timestamptz` | Stored as TEXT | EF Core value converters with enforced UTC |

**What production would use.** PostgreSQL. The order domain is transactional and money-bearing, so
a relational ACID store is correct regardless — eventual consistency is not acceptable for whether
a customer was charged. PostgreSQL adds `FOR UPDATE SKIP LOCKED` (the natural fit for the
scheduler), MVCC so readers never block writers, `xmin` for free optimistic concurrency, native
`numeric`, and true multi-writer concurrency. The migration path is deliberately narrow: a second
`IPendingOrderClaimer` adapter and a provider swap, with no change to the domain, application
layer, or API contract.

> A production e-commerce platform would be polyglot: relational for orders and payments,
> Elasticsearch for product search, Redis for carts and sessions, and a warehouse for analytics.
> This service owns the **orders** bounded context, which is squarely relational.

### 3.2 Zero-infrastructure constraint

**The system must run with `dotnet run` and nothing else** — no Docker, no database server, no
cloud resources, no message broker, no external identity provider.

This is a deliberate constraint on the *deliverable*, not a simplification of the *design*. Every
functional requirement in §7, the whole API contract in §11, the security model in §8 and the
state machine in §6 are delivered exactly as specified. The constraint is absorbed entirely below
the repository and scheduler ports.

Consequences, stated plainly so they are not mistaken for oversights:

- The database is a local SQLite file (`orders.db`), created and migrated automatically on first run
- Seed data (products, two customers, one admin) is applied at startup so the API is immediately usable
- JWT tokens are signed with a symmetric development key from configuration; no external identity provider
- Notifications and payments remain out of scope (§1.2) rather than being stubbed, since a fake
  implementation of an unspecified integration demonstrates nothing
- Single-process deployment; horizontal scaling is a design property (§10.3), not something
  demonstrated at runtime

**What is *not* mocked.** The database is real, the transactions are real, the concurrency control
is real, and the scheduler genuinely claims and promotes rows. Nothing in the order lifecycle is
faked or short-circuited — a stubbed repository returning canned objects would make the tests in
§12.2 meaningless. "No infrastructure" here means *no external process to install*, not *no real
behaviour*.

---

## 4. Architecture

Clean-architecture layering with dependencies pointing inward. This is more structure than five
endpoints strictly require; it is adopted because the separation of domain rules from
infrastructure is a primary evaluation criterion.

```
src/
  OrderProcessing.Domain/          — entities, Money value object, state machine, domain errors
                                     (no external dependencies)
  OrderProcessing.Application/     — use cases (OrderService, OrderPromotionService), DTOs,
                                     port interfaces (IOrderRepository, IProductCatalog,
                                     IPendingOrderClaimer, IOrderNumberGenerator), OrderScope
  OrderProcessing.Infrastructure/  — EF Core + SQLite adapters, claim adapter, auth,
                                     background-job host
  OrderProcessing.Api/             — controllers, middleware, claims mapping, composition root
tests/
  OrderProcessing.Domain.Tests/        — domain rules and architecture rules, no I/O
  OrderProcessing.Application.Tests/   — use cases against substituted ports, no database
  OrderProcessing.Integration.Tests/   — full stack against a real SQLite database
```

**Dependency rule:** `Api → Infrastructure → Application → Domain`. The domain references nothing,
and the application layer references no persistence or web library — only ports it defines itself.

**Enforced, not merely documented.** `ArchitectureTests` asserts the dependency rule at build time:
the domain and application assemblies are checked for EF Core, ASP.NET and provider references, and
the order use cases are asserted to live in the application assembly. A layer name is a claim, and
claims need tests — an earlier revision of this solution placed `OrderService` in infrastructure
while this document described it as an application concern, and nothing caught the divergence.

**Where responsibilities sit.** The promotion job is the clearest illustration of the split:

| Decision | Layer | Type |
| --- | --- | --- |
| *When* a run happens | Infrastructure | `OrderPromotionBackgroundService` (timer, host lifetime, scope) |
| *What* a run does | Application | `OrderPromotionService` (batching, failure isolation, audit) |
| *How* rows are claimed | Infrastructure | `SqlitePendingOrderClaimer` (provider-specific SQL) |

**Why the ports matter here.** The zero-infrastructure constraint (§3.2) makes this layering
load-bearing rather than decorative: the database-specific parts of the design — the claim
strategy (§9.3) and the concurrency token (§10.2) — are exactly the parts that differ between
SQLite and PostgreSQL. Confining them behind ports is what keeps the domain, the use cases and
the test suite identical across both.

**Transaction boundary:** one transaction per use case, committed through
`IOrderRepository.SaveChangesAsync`. The background job uses one transaction *per batch* (§9.4).
Provider concurrency exceptions are translated to `ConcurrencyConflictException` at the repository
boundary, so no layer above persistence needs to know which ORM is in use.

---

## 5. Domain model

### 5.1 Entities

**Customer**

| Field | Type (SQLite) | Notes |
| --- | --- | --- |
| `Id` | `TEXT` (GUID) | PK |
| `Email` | `TEXT` | Unique, case-insensitive (`NOCASE` collation) |
| `FullName` | `TEXT` | Max length 200, enforced by EF + validation |
| `CreatedAt` | `TEXT` (ISO-8601 UTC) | |

**Product**

| Field | Type (SQLite) | Notes |
| --- | --- | --- |
| `Id` | `TEXT` (GUID) | PK |
| `Sku` | `TEXT` | Unique |
| `Name` | `TEXT` | Max length 200 |
| `UnitPriceMinor` | `INTEGER` | Price in minor units (cents) — see §5.4 |
| `Currency` | `TEXT` | ISO 4217, 3 chars |
| `IsActive` | `INTEGER` (0/1) | Inactive products cannot be ordered |

**Order**

| Field | Type (SQLite) | Notes |
| --- | --- | --- |
| `Id` | `TEXT` (GUID) | PK |
| `OrderNumber` | `TEXT` | Human-readable, unique (e.g. `ORD-2026-000042`) |
| `CustomerId` | `TEXT` (GUID) | FK → Customer, indexed |
| `Status` | `TEXT` | Enum stored as string for readability, indexed |
| `TotalAmountMinor` | `INTEGER` | Always computed server-side (§5.4) |
| `Currency` | `TEXT` | ISO 4217 |
| `IdempotencyKey` | `TEXT` | Nullable; unique per customer (filtered index) |
| `CreatedAt` / `UpdatedAt` | `TEXT` (ISO-8601 UTC) | |
| `CancelledAt` | `TEXT` (ISO-8601 UTC) | Nullable |
| `CancelledBy` | `TEXT` | Nullable — `Customer` / `Admin` / `System` |
| `CancellationReason` | `TEXT` | Nullable; mandatory for admin cancellations |
| `Version` | `INTEGER` | Application-managed concurrency token (§10.2) |

**OrderItem**

| Field | Type (SQLite) | Notes |
| --- | --- | --- |
| `Id` | `TEXT` (GUID) | PK |
| `OrderId` | `TEXT` (GUID) | FK → Order, indexed, cascade delete |
| `ProductId` | `TEXT` (GUID) | FK → Product |
| `ProductName` | `TEXT` | **Snapshot** at order time |
| `UnitPriceMinor` | `INTEGER` | **Snapshot** at order time |
| `Quantity` | `INTEGER` | `> 0`, enforced by check constraint |
| `LineTotalMinor` | `INTEGER` | `UnitPriceMinor × Quantity`, persisted for auditability |

**OrderStatusHistory**

| Field | Type (SQLite) | Notes |
| --- | --- | --- |
| `Id` | `TEXT` (GUID) | PK |
| `OrderId` | `TEXT` (GUID) | FK → Order, indexed |
| `FromStatus` / `ToStatus` | `TEXT` | `FromStatus` null on creation |
| `ChangedBy` | `TEXT` | `Customer` / `Admin` / `System` |
| `ChangedByUserId` | `TEXT` (GUID) | Nullable — null for system transitions |
| `Reason` | `TEXT` | Nullable, max length 500 |
| `ChangedAt` | `TEXT` (ISO-8601 UTC) | |

> **Note on types.** SQLite has no native `uuid`, `timestamptz`, `decimal` or `boolean` types.
> GUIDs and timestamps are persisted as TEXT via EF Core value converters, with timestamps
> normalized to UTC on write and read back as `DateTimeOffset`. Foreign keys are enforced
> (`PRAGMA foreign_keys = ON`). Under PostgreSQL these become `uuid`, `timestamptz`, `bigint` and
> `boolean` with no change above the persistence layer.

### 5.2 Why price snapshots matter

Order items copy `ProductName` and `UnitPrice` from the catalog at creation time rather than
joining to `Product` at read time. If the catalog price later changes, historical orders must
still reflect what the customer actually agreed to pay. Recomputing an old order's total from
current prices would be a correctness and compliance defect.

### 5.3 Indexes

| Index | Purpose |
| --- | --- |
| `IX_Orders_CustomerId_CreatedAt` | The dominant query: a customer's orders, newest first |
| `IX_Orders_Status_CreatedAt` | Admin filtering by status, and the scheduler's claim query |
| `IX_Orders_CustomerId_TotalAmountMinor` | Supports the `totalAmount` sort in FR-4.7 |
| `UX_Orders_CustomerId_IdempotencyKey` (unique, filtered on non-null) | Idempotent creation lookup, scoped per customer |
| `UX_Orders_OrderNumber` (unique) | Human-readable lookup and uniqueness guarantee |
| `IX_OrderItems_OrderId` | Loading items for an order |
| `IX_OrderStatusHistory_OrderId_ChangedAt` | Rendering an order's audit trail |

### 5.4 Money handling — integer minor units

**Money is stored as `INTEGER` in minor units (cents), never as a floating-point or decimal
column.** `$49.99` is persisted as `4999`. A `Money` value object in the domain encapsulates the
amount and currency, exposes arithmetic that cannot silently lose precision, and handles
formatting at the API boundary — so the representation never leaks into business logic.

This resolves a defect that would otherwise be shipped silently. SQLite has no native `decimal`
type, and EF Core maps `decimal` to TEXT to preserve precision. TEXT sorts
*lexicographically*: `"9.99"` sorts after `"100.00"`, so **FR-4.7 (sort by `totalAmount`) would
return wrong results** — and wrong pagination on top of it. EF Core surfaces this as a warning
about unsupported `decimal` ordering, not an error, so it is easy to miss. Mapping to REAL instead
would trade a sorting bug for a precision bug, reintroducing floating-point error into money.

Integer minor units avoid both: they sort correctly, compare correctly, aggregate exactly, and
carry no rounding error. This is also what payment processors (Stripe, Adyen, PayPal) do, so it is
the conventional choice rather than a workaround — the SQLite constraint simply forced a good
decision earlier than it would otherwise have been made.

Rules that follow:

- All persisted monetary values are `INTEGER` minor units, suffixed `…Minor` in the schema
- The API contract continues to expose decimal values (`149.97`); conversion happens in the
  mapping layer only
- `LineTotalMinor = UnitPriceMinor × Quantity` — integer arithmetic, exact by construction
- `TotalAmountMinor = Σ LineTotalMinor`, always recomputed server-side and never trusted from input
- Currency is stored alongside every monetary value; arithmetic across differing currencies throws
- Floating-point types are never used for money anywhere in the system

**An order's currency is derived, not supplied.** `Order.Create` reads the distinct currencies of
its lines and rejects the order unless there is exactly one. An earlier revision accepted the
currency as a parameter, and the caller passed the *first requested product's* — so a later line in
another currency was measured against an arbitrary choice, and the resulting mismatch threw a bare
`InvalidOperationException` that surfaced as `500`.

Deriving it makes "every line shares one currency" an invariant the aggregate enforces rather than
a precondition it trusts, and `MixedCurrencyOrderException` is a `DomainException` so it maps to
`422` with the currencies named. Multi-currency remains out of scope (§1.2); this is the explicit
rejection that scope decision implies, rather than an implicit and order-dependent one.

---

## 6. Order state machine

### 6.1 States

| State | Meaning |
| --- | --- |
| `PENDING` | Created, awaiting processing. Initial state for all orders |
| `PROCESSING` | Picked up for fulfilment — set by the background job or an admin |
| `SHIPPED` | Dispatched to the customer |
| `DELIVERED` | Received by the customer. Terminal |
| `CANCELLED` | Cancelled before dispatch. Terminal |

### 6.2 Transition matrix

Transitions are governed by **both** the source state and the actor's role. This is stricter than
the brief requires and is the central invariant of the domain.

| From → To | `PROCESSING` | `SHIPPED` | `DELIVERED` | `CANCELLED` |
| --- | --- | --- | --- | --- |
| **`PENDING`** | Admin, System | ✗ | ✗ | Customer, Admin |
| **`PROCESSING`** | ✗ | Admin | ✗ | Admin only |
| **`SHIPPED`** | ✗ | ✗ | Admin | ✗ |
| **`DELIVERED`** | ✗ | ✗ | ✗ | ✗ (terminal) |
| **`CANCELLED`** | ✗ | ✗ | ✗ | ✗ (terminal) |

Skip-transitions (e.g. `PENDING → SHIPPED`) are rejected. Backward transitions are rejected.

### 6.3 Cancellation policy

| Actor | `PENDING` | `PROCESSING` | `SHIPPED` | `DELIVERED` | `CANCELLED` |
| --- | --- | --- | --- | --- | --- |
| **Customer** | ✅ Allowed | ❌ `409` — already being fulfilled | ❌ `409` | ❌ `409` | ⟳ `200` no-op |
| **Admin** | ✅ Allowed | ✅ Allowed (reason required) | ❌ `409` — returns flow | ❌ `409` | ⟳ `200` no-op |

**Why re-cancelling is a no-op rather than a conflict.** Cancelling an order that is already
cancelled requests a state the system is already in, so there is nothing to reject. Returning
`200` makes the endpoint safe to retry — a double-tapped button or a client retrying after a
network timeout succeeds instead of surfacing a spurious error. The original `CancelledBy`,
`CancellationReason` and `CancelledAt` are never overwritten, so the protection that matters is
preserved. This is the same rule as FR-3.5 applied consistently rather than a special case for
cancellation.

**Rationale for the admin override.** The brief only grants cancellation to customers in
`PENDING`. In practice, operations staff must be able to cancel an in-flight order — fraud
review, a payment that failed late, a customer phoning support, or a stock discrepancy. Granting
admins a wider window models this reality while keeping the customer rule exactly as specified.

**Rationale for capping at `PROCESSING`.** Once an order has `SHIPPED`, cancellation is no longer
meaningful: goods are physically in transit and the correct process is a return/refund with its
own reversal, restocking and refund semantics. Modelling that as a "cancel" would misrepresent
the business process. Returns are explicitly out of scope (§1.2).

Every cancellation records `CancelledBy`, `CancellationReason` and `CancelledAt`, plus an
`OrderStatusHistory` row. The reason is **mandatory for admin cancellations** and optional for
customers — an override is only defensible if it is attributable.

### 6.4 Enforcement

The transition matrix is implemented **once**, as a guard inside the `Order` aggregate
(`Order.TransitionTo(newStatus, actor, reason)`). Controllers, use cases and the background job
all route through it. No validation logic lives in a controller.

Illegal transitions raise a domain exception mapped to `409 Conflict` — the request was
well-formed but conflicts with current state, which is semantically distinct from `400`.

---

## 7. Functional requirements

Each requirement is phrased to be directly testable.

### FR-1 — Create an order

| # | Requirement |
| --- | --- |
| FR-1.1 | An authenticated customer can create an order with one or more items |
| FR-1.2 | An order with zero items is rejected with `400` |
| FR-1.3 | Each item must reference an existing, active product, else `400` (or `422`) |
| FR-1.4 | Quantity must be an integer `≥ 1`; a configurable per-line maximum applies |
| FR-1.5 | `ProductName` and `UnitPrice` are resolved server-side from the catalog |
| FR-1.5a | All items must share one currency; a mixed-currency request is rejected with `422` naming the currencies involved. The order's currency is **derived** from its lines, never supplied by the caller |
| FR-1.6 | Any client-supplied price is **ignored**, never trusted |
| FR-1.7 | `LineTotal = UnitPrice × Quantity`; `TotalAmount = Σ LineTotal` |
| FR-1.8 | Duplicate product IDs in one request are merged, summing their quantities |
| FR-1.9 | New orders are created as `PENDING` |
| FR-1.10 | A unique, human-readable `OrderNumber` is assigned |
| FR-1.10a | Concurrent creations receive distinct order numbers; a collision is retried, never surfaced |
| FR-1.11 | An initial `OrderStatusHistory` row (`null → PENDING`) is written |
| FR-1.12 | Repeating a request with the same `Idempotency-Key` returns the original order, not a duplicate |
| FR-1.12a | The guarantee holds under concurrency: several in-flight requests sharing a key yield exactly one order, one `201` and the rest `200` |
| FR-1.13 | The order is attributed to the authenticated caller; a `customerId` in the body is ignored |
| FR-1.14 | Responds `201 Created` with a `Location` header |

### FR-2 — Retrieve an order

| # | Requirement |
| --- | --- |
| FR-2.1 | `GET /orders/{id}` returns the order with its items and computed total |
| FR-2.2 | A customer can retrieve only their own orders |
| FR-2.3 | Requesting another customer's order returns `404`, **not** `403` (§8.4) |
| FR-2.4 | An admin can retrieve any order |
| FR-2.5 | An unknown ID returns `404` |
| FR-2.6 | The response includes the status history |

### FR-3 — Update order status

| # | Requirement |
| --- | --- |
| FR-3.1 | `PATCH /orders/{id}/status` is restricted to the `Admin` role; `403` otherwise |
| FR-3.2 | The transition is validated against the matrix in §6.2 |
| FR-3.3 | An illegal transition returns `409` with the attempted and permitted transitions |
| FR-3.4 | A successful transition writes an `OrderStatusHistory` row |
| FR-3.5 | Transitioning to the same status is an idempotent no-op returning `200`, leaving the version and history untouched |
| FR-3.6 | Transitions from a terminal state always return `409` |

### FR-4 — List orders

| # | Requirement |
| --- | --- |
| FR-4.1 | `GET /orders` returns a paginated list |
| FR-4.2 | Optional `status` filter, validated against the enum |
| FR-4.3 | A customer's results are always scoped to their own orders |
| FR-4.4 | A customer cannot widen scope via `?customerId=`; the parameter is ignored |
| FR-4.5 | An admin may filter by `?customerId=` |
| FR-4.6 | Default page size 20, maximum 100; a larger request is clamped, not rejected |
| FR-4.7 | Sortable by `createdAt` or `totalAmount`, ascending or descending; default `createdAt` desc |
| FR-4.8 | The response envelope includes `page`, `pageSize`, `totalCount`, `totalPages` |
| FR-4.9 | An empty result returns `200` with an empty array, not `404` |

### FR-5 — Cancel an order

| # | Requirement |
| --- | --- |
| FR-5.1 | `POST /orders/{id}/cancel` cancels per the policy in §6.3 |
| FR-5.2 | A customer may cancel only their own order, and only from `PENDING` |
| FR-5.3 | An admin may cancel from `PENDING` or `PROCESSING` |
| FR-5.4 | A reason is mandatory for admin cancellations; `400` if absent |
| FR-5.5 | Cancelling from a disallowed state returns `409` |
| FR-5.6 | Cancellation records `CancelledBy`, `CancellationReason`, `CancelledAt` and a history row |
| FR-5.7 | Cancelling an already-cancelled order is an idempotent no-op returning `200`, and never overwrites the original `CancelledBy`, `CancellationReason` or `CancelledAt` |

### FR-6 — Background status promotion

| # | Requirement |
| --- | --- |
| FR-6.1 | A background job runs every 5 minutes (configurable) |
| FR-6.2 | It promotes `PENDING` orders to `PROCESSING` |
| FR-6.3 | Orders are claimed in bounded batches, never loaded wholesale |
| FR-6.4 | Concurrent instances never process the same order twice |
| FR-6.5 | Each promotion writes an `OrderStatusHistory` row with `ChangedBy = System` |
| FR-6.6 | An order cancelled between claim and commit is not promoted |
| FR-6.7 | A failure on one order does not roll back the whole batch |
| FR-6.8 | Each run emits structured logs and metrics (claimed, promoted, failed, duration) |
| FR-6.9 | The job shuts down gracefully on cancellation, finishing or abandoning cleanly |

---

## 8. Security model

Three distinct layers. Conflating them is the most common source of access-control defects.

### 8.1 Authentication

JWT bearer tokens. The `sub` claim carries the customer ID and the `role` claim carries
`Customer` or `Admin`. A **development-only** token endpoint issues test tokens; it is disabled
outside the `Development` environment.

### 8.2 Authorization — role level

*"May this kind of actor perform this operation?"* Enforced with policy-based authorization
attributes, e.g. `[Authorize(Policy = "AdminOnly")]` on `PATCH /orders/{id}/status`.

### 8.3 Authorization — resource level (ownership)

*"May this specific actor touch this specific row?"* Passing §8.2 while failing §8.3 is the
**IDOR / Broken Object-Level Authorization** defect — the top item on the OWASP API Security Top 10.

Two rules:

1. **Identity comes from the token, never from the request.** A `customerId` in a body or query
   string is ignored for customers. *The caller never declares who they are.*
2. **Scope the query; do not fetch-then-compare.** Ownership is pushed into the predicate
   (`WHERE Id = @id AND CustomerId = @callerId`) rather than loading the row and checking
   afterwards. Fetch-then-compare works until someone adds an endpoint and forgets the check;
   query-scoping is fail-closed by construction.

This is applied **centrally** — via an EF Core global query filter and a single query-scope
helper — so a new endpoint cannot silently omit it.

### 8.4 Why `404` rather than `403` for another customer's order

A `403` confirms that the resource exists, letting an attacker enumerate valid order IDs and
infer order volumes. A `404` reveals nothing. The caller cannot distinguish "does not exist" from
"not yours", which is the desired property. `403` is still used where the *operation* is
forbidden regardless of ownership (e.g. a customer calling the admin status endpoint), since that
leaks nothing.

### 8.5 Supporting controls

- HTTPS enforced; HSTS in production
- Rate limiting on write endpoints (ASP.NET Core rate limiter)
- Validation on every input; parameterized queries throughout via EF Core
- No secrets in source; configuration via environment variables / user-secrets
- Structured logs exclude PII and never log tokens
- Permissive CORS disabled by default

---

## 9. Background job design

### 9.1 Mechanism

A hosted `BackgroundService` driven by a `PeriodicTimer`, resolving a scoped service per run.
Quartz.NET and Hangfire were considered; a direct implementation was chosen because the
concurrency handling *is* the interesting part of this requirement, and delegating it to a
library would obscure the reasoning. Both would also have added persistent storage and, for
Hangfire, a dashboard process — infrastructure the zero-dependency constraint in §3.2 rules out.
Quartz with clustering would be the production choice once richer scheduling (cron expressions,
misfire policies, an operations dashboard) is needed.

### 9.2 Testability

The job depends on `TimeProvider` (built into the BCL since .NET 8) rather than `DateTime.UtcNow`, and the
timer is abstracted behind an interface. Tests use a fake time provider to advance the clock
deterministically — a 5-minute schedule is verified in milliseconds, with no `Thread.Sleep`.

### 9.3 Claiming and promoting

Each batch runs in **one transaction** and separates two concerns that an earlier revision
conflated:

| Step | Concern | Where it lives |
| --- | --- | --- |
| **Claim** | Who may work on these orders — concurrency, provider-specific | `IPendingOrderClaimer` |
| **Promote** | What state they move to — a business rule | `Order.PromoteToProcessing` |
| **Commit** | Status and audit entry persist together | `IOrderRepository` |

**A claim is a lock, not a transition.** Claiming does not change an order's status; it reserves
the right to promote it. Promotion then goes through the aggregate, so the transition matrix in
section 6.2 remains the single point of enforcement and the audit entry is written by the same
code path that serves the API.

**SQLite adapter (this build) — lease stamping.** SQLite has no `FOR UPDATE SKIP LOCKED`, so the
lock is taken by writing. A single atomic statement stamps a lease onto pending rows and reports
which ones it stamped; because SQLite serialises writes, it cannot interleave with another
worker's:

```sql
UPDATE orders
SET "PromotionLease" = $lease
WHERE "Id" IN (
    SELECT "Id" FROM orders
    WHERE "Status" = 'Pending' AND "PromotionLease" IS NULL
    ORDER BY "CreatedAt"
    LIMIT $batchSize
)
AND "Status" = 'Pending' AND "PromotionLease" IS NULL
RETURNING "Id";
```

The lease is an EF Core **shadow property**: present in the database and the persistence model but
absent from the `Order` aggregate, so a scheduling mechanism cannot leak into the domain. It is
cleared in the transaction that promotes the order, so it never outlives the work it guards —
there are no stale leases to reap, and an abandoned run leaves no trace.

**PostgreSQL adapter (production) — `FOR UPDATE SKIP LOCKED`.** Under a true multi-writer engine
each worker locks a disjoint row set and steps over rows another worker already holds, so workers
progress in parallel. No lease column is needed, because the engine provides row locks directly:

```sql
SELECT id FROM orders
WHERE status = 'Pending'
ORDER BY created_at
LIMIT @batchSize
FOR UPDATE SKIP LOCKED;
```

**Why the port exists.** Both strategies satisfy one contract — claim at most `batchSize` pending
orders, exactly once, releasing the claim if the transaction is abandoned — while relying on
different guarantees. Isolating the difference keeps the promotion use case, the domain and every
test unchanged across providers.

**What an earlier revision did, and why it changed.** The first implementation claimed and
transitioned in one statement (`UPDATE ... SET Status = 'Processing' ... RETURNING Id`). It was
atomic and behaved correctly, but it carried two defects:

1. It **restated the transition rule in SQL**, so section 6.2 was no longer the single point of
   enforcement — the rule existed twice, in two languages, free to diverge.
2. It committed the status change **independently of the audit entry**, because no transaction
   spanned the two writes. An interrupted run could leave an order `PROCESSING` with no history
   row, violating FR-6.5.

The second is the more serious, and is why the design changed rather than merely the prose
describing it. Both are now covered by `PromotionIntegrityTests`.

**Honest limitation.** SQLite's single-writer lock means concurrent workers serialise rather than
parallelise. Correctness is preserved — no order is promoted twice — but throughput does not scale
with workers. This is a scaling ceiling, not a correctness gap, and it is the principal reason a
production deployment would move to PostgreSQL (section 3.1).
### 9.4 Execution model

- **Bounded batches** (default 100, configurable) loop until no `PENDING` orders remain or a
  per-run cap is reached, bounding memory and transaction duration
- **One transaction per batch** spanning claim, promotion and commit, so a status change and its
  audit entry are never written apart (FR-6.5), and a large backlog does not hold a single long
  transaction
- **Failure isolation** — a per-order failure is logged and skipped; the rest of the batch commits
- **Non-overlapping runs** — a run exceeding the interval is not re-entered; the next tick is skipped
- **The aggregate re-checks the transition**, so an order cancelled between claim and promotion is
  rejected by the matrix rather than silently overwritten (FR-6.6)
- **Claims released on abandonment** — an uncommitted run rolls back, returning its orders to the
  pool with no residue
- **Graceful shutdown** honouring the host `CancellationToken`

### 9.5 Observability

Per run: `orders_claimed`, `orders_promoted`, `orders_failed`, `run_duration_ms`, correlated by a
run ID. Consecutive failures raise the log level so the job cannot fail silently — a background
job that stops working without anyone noticing is a common production failure mode.

---

## 10. Non-functional requirements

### 10.1 Performance

| Metric | Target |
| --- | --- |
| `GET /orders/{id}` | p95 < 100 ms |
| `GET /orders` (paged) | p95 < 200 ms |
| `POST /orders` | p95 < 300 ms |
| Background batch (100 orders) | < 2 s |

Supported by indexes (§5.3), pagination with a hard cap, projection to DTOs rather than loading
full aggregates for list queries, and `AsNoTracking()` on reads.

### 10.2 Concurrency and correctness

An **application-managed optimistic concurrency token** on `Order` makes lost updates impossible.
SQLite has no server-generated `rowversion`, so `Order.Version` is an `INTEGER` marked
`IsConcurrencyToken()` and incremented in an overridden `SaveChangesAsync`. EF Core then appends
`WHERE Version = @original` to every update; a zero-row result raises
`DbUpdateConcurrencyException`. The guarantee is identical to PostgreSQL's `xmin` — only the
increment is explicit rather than implicit, and the entity model is unchanged when providers swap.

The specific race worth calling out: **an admin cancels an order at the same moment the scheduler
promotes it from `PENDING` to `PROCESSING`.** One transaction commits; the other detects the
version mismatch, reloads and re-evaluates against the new state. The outcome is deterministic
rather than timing-dependent, and it is covered by an explicit integration test (§12.2, case 4).

Concurrency conflicts surface as `409 Conflict` with a retry hint.

### 10.3 Reliability and availability

The API is written to be stateless and horizontally scalable — no session affinity, no in-process
mutable state, identity carried in the token. Liveness (`/health/live`) and readiness
(`/health/ready`, including a database probe) endpoints are exposed, and graceful shutdown drains
in-flight requests and stops the scheduler cleanly.

**Stated honestly:** under SQLite this is a *single-node* deployment. Horizontal scalability is a
property of the code, not something demonstrated at runtime, because a local database file cannot
be shared across instances. The scheduler's claim logic is nonetheless written to be multi-worker
safe (§9.3) and is tested with concurrent workers in-process (§12.2, case 5), so the guarantee
holds the moment the store is swapped for a server database. Distinguishing "designed for" from
"demonstrated at" is deliberate — claiming horizontal scale on a single-file database would be
misleading.

Transient failures are retried with exponential backoff via EF Core's execution strategy. Under
PostgreSQL this extends to connection pooling with a bounded pool and retry on transient network
faults.

### 10.4 Observability

Structured logging (Serilog) in JSON, with a correlation ID propagated through every request and
returned in error responses. Metrics exposed via `System.Diagnostics.Metrics`. OpenTelemetry
tracing hooks included so the service can be wired into a collector.

### 10.5 Maintainability

Nullable reference types and `TreatWarningsAsErrors` enabled. Analyzers and `.editorconfig`
enforced in CI. A target of roughly 80% coverage on Domain and Application — though a small
number of sharp tests covering the real invariants is worth more than a high percentage
(see §11).

### 10.6 API versioning

The URL is versioned from the outset (`/api/v1/...`). Retrofitting versioning after release is
considerably more disruptive than adopting it on day one.

---

## 11. API contract

> The complete reference — request and response schemas, every query parameter, all status and
> error codes — lives in [API.md](./API.md). This section records the shape of the contract and the
> reasoning behind it.

Base path `/api/v1`. All endpoints require authentication unless stated otherwise.

| Method | Path | Role | Purpose |
| --- | --- | --- | --- |
| `POST` | `/orders` | Customer | Create an order |
| `GET` | `/orders/{id}` | Customer (own) / Admin | Retrieve an order |
| `GET` | `/orders` | Customer (own) / Admin | List with filter, sort, pagination |
| `PATCH` | `/orders/{id}/status` | Admin | Transition status |
| `POST` | `/orders/{id}/cancel` | Customer (own) / Admin | Cancel |
| `GET` | `/products` | Any | Browse the catalog |
| `GET` | `/health/live`, `/health/ready` | Anonymous | Health probes |
| `POST` | `/dev/token` | Anonymous | **Development only** — issue a test token |

### 11.1 Create an order

```http
POST /api/v1/orders
Authorization: Bearer <jwt>
Idempotency-Key: 6f1c2f84-1f3a-4a1b-9c62-3a5f0f6a1d22
Content-Type: application/json

{
  "items": [
    { "productId": "3f2504e0-4f89-11d3-9a0c-0305e82c3301", "quantity": 2 },
    { "productId": "9c858901-8a57-4791-81fe-4c455b099bc9", "quantity": 1 }
  ]
}
```

```http
HTTP/1.1 201 Created
Location: /api/v1/orders/7b1e...

{
  "id": "7b1e...",
  "orderNumber": "ORD-2026-000042",
  "customerId": "c1a2...",
  "status": "PENDING",
  "currency": "USD",
  "totalAmount": 149.97,
  "items": [
    {
      "productId": "3f2504e0-4f89-11d3-9a0c-0305e82c3301",
      "productName": "Mechanical Keyboard",
      "unitPrice": 49.99,
      "quantity": 2,
      "lineTotal": 99.98
    }
  ],
  "createdAt": "2026-09-25T11:30:00Z"
}
```

### 11.2 Idempotent creation

A double-clicked checkout button must not create two orders. The client supplies an
`Idempotency-Key` header; the key is stored unique-per-customer. A repeat request with the same
key returns the **original** order with `200 OK` rather than creating a duplicate. A key reused
with a *different* payload returns `422 Unprocessable Entity`.

**The read cannot enforce this on its own.** Checking for an existing key before inserting handles
a sequential retry, but two concurrent requests can both miss that read before either writes. The
unique index is therefore the real enforcement, and the constraint violation is treated as a
*signal* rather than an error: the losing request re-reads the key and returns the order the
winner created. Twelve concurrent requests sharing a key produce one `201`, eleven `200`s and
exactly one order.

The same pattern applies to `OrderNumber`. Numbers are allocated as "highest for the current year,
plus one", so concurrent callers can read the same maximum. The collision is caught by the unique
index and the allocation is retried, bounded at five attempts. A production deployment would use a
database sequence and avoid the contention entirely — this is noted as a known limitation rather
than presented as the ideal design.

> **Why this is worth stating.** Read-then-write is the default shape of an idempotency check, and
> it passes every sequential test. The failure only appears under genuine concurrency, which is
> precisely the situation the feature exists to survive.

### 11.3 Error format

All errors use RFC 7807 `ProblemDetails`, extended with a correlation ID:

```json
{
  "type": "https://orderprocessing/errors/invalid-status-transition",
  "title": "Invalid status transition",
  "status": 409,
  "detail": "Cannot transition order from DELIVERED to CANCELLED.",
  "instance": "/api/v1/orders/7b1e.../cancel",
  "correlationId": "0HMV9K2QJ8..."
}
```

Validation failures return `400` with a per-field `errors` dictionary.

### 11.4 Status code conventions

| Code | Used when |
| --- | --- |
| `200` | Successful read, or an accepted no-op |
| `201` | Order created |
| `400` | Malformed request or validation failure |
| `401` | Missing or invalid token |
| `403` | Operation forbidden for the caller's role |
| `404` | Not found, **or** not owned by the caller (§8.4) |
| `409` | Illegal state transition, or a concurrency conflict |
| `422` | Semantically invalid — e.g. inactive product, idempotency key reuse with a new payload |
| `429` | Rate limit exceeded |

---

## 12. Test strategy

The brief's objective names testing explicitly, so it is treated as a first-class deliverable.

### 12.1 Layers

| Layer | Scope | Tooling |
| --- | --- | --- |
| **Unit — domain** | State machine, `Money` arithmetic, invariants | xUnit, Shouldly |
| **Unit — application** | Use-case orchestration | xUnit, NSubstitute |
| **Integration — API** | Full stack against a real SQLite database | `WebApplicationFactory` + SQLite |
| **Integration — scheduler** | Claiming, batching, concurrency | SQLite + fake `TimeProvider` |

### 12.2 Cases that must exist

These are the tests that demonstrate the design was reasoned about rather than assumed.

1. **Exhaustive transition matrix** — every (from-state × to-state × role) combination, legal and
   illegal, driven from a single theory data source
2. **Cross-customer isolation** — customer A requests customer B's order and receives `404`, not
   `403` and not the data
3. **Scope widening** — customer A passes `?customerId=B` on the list endpoint and still sees only
   their own orders
4. **Cancel-vs-scheduler race** — an admin cancel and a scheduler promotion issued concurrently
   against the same order produce a deterministic outcome with no lost update
5. **Concurrent scheduler workers** — two workers running against a shared backlog promote every
   order exactly once, with no duplicates and none missed
6. **Idempotent creation** — the same `Idempotency-Key` twice yields one order
7. **Price tampering** — a client-supplied `unitPrice` in the create payload is ignored
8. **Price-snapshot integrity** — changing a catalog price does not alter an existing order's total
9. **Batch failure isolation** — one poison order does not prevent the rest of the batch committing
10. **Page-size clamping** — `?pageSize=10000` is clamped to the maximum rather than rejected

### 12.3 Approach

Tests run against **real SQLite**, not the EF Core InMemory provider. This matters: the InMemory
provider ignores unique constraints, foreign keys and check constraints, so tests for FR-1.12
(idempotency uniqueness), FR-1.3 (product integrity) and every concurrency case would pass without
exercising anything. A test suite that cannot fail is worse than no suite at all.

Each test class gets an isolated SQLite database — in-memory with a private connection for speed,
or a temporary file where multiple connections must observe the same data (the concurrent-worker
tests). Migrations are applied per fixture, so the schema under test is the schema that ships.

**Isolation is asserted, not assumed.** `ApiFactory` checks at startup that the `DbContext`
actually resolved an in-memory connection string and fails loudly otherwise. This guard exists
because it did not hold: an eagerly-read connection string meant the override was ignored and the
suite silently ran against a file on disk that accumulated across runs. Tests were passing, but
they were not independent, and results depended on execution history. A claimed isolation property
is worth as much as a claimed layering property — which is to say, only as much as the check that
enforces it.

Test data is built with the builder pattern to keep intent legible. Tests are independent and
parallelizable. No `Thread.Sleep` anywhere — time is always controlled through `TimeProvider`, so
a five-minute schedule is verified in milliseconds.

**Delivered:** 298 tests (222 domain including architecture rules, 21 application, 55 integration),
all passing.

### 12.4 Verifying that the tests can fail

A suite that has never been observed failing is an assumption, not evidence. Two guards apply to
the tests covering the central invariants.

**Independently-declared expectations.** The transition matrix test enumerates all 75
`(actor × from × to)` combinations and checks each against an expectation written longhand in the
test file. Deriving those expectations from `OrderStatusTransitions` would be tautological — the
test would pass for any implementation, including one that permitted every transition.

**A reproducible mutation audit.** `tools/mutation-audit.ps1` breaks each invariant in turn, runs
the suite, restores the source and reports what failed. Requires PowerShell 7+ (`pwsh`), which is
cross-platform. It is a script rather than a prose claim because a recorded mutation result
describes the code as it was on the day it was run, and quietly stops being true when that code
changes — which is exactly what happened here (see below).

The table below lists the same eight mutations the script defines, so the two cannot drift apart:

| Invariant broken | Tests failed |
| --- | --- |
| Promotion bypasses the transition matrix (§6.2) | 8 |
| Order currency reverts to "first line wins" (§5.4) | 6 |
| Customers granted the admin cancellation window (§6.3) | 5 |
| Ownership scoping removed (§8.3) | 3 |
| Claim drops the pending-status filter (§9.3) | 2 |
| Unique-violation translation disabled (§11.2) | 2–3 ¹ |
| Claim no longer requires an enclosing transaction (§9.3) | 1 ² |
| Claim drops the lease guard (§9.3) | 0 ³ |

¹ Not deterministic. The underlying test races several concurrent requests, so the number of
failing assertions depends on which lose. Reported as a range rather than a convenient single
figure.

² **Was zero until a test was added.** The guard is load-bearing: without an enclosing transaction
the lease commits independently, so a worker that died before promoting would leave rows leased
indefinitely, and the status change could again commit apart from its audit entry — the defect
§9.3 was redesigned to remove. The contract was enforced in code and asserted nowhere, and only
the audit surfaced that.

³ **Genuinely zero.** Removing `AND "PromotionLease" IS NULL` fails no test because it changes no
behaviour. The lease is cleared in the transaction that promotes, so no committed row carries one
and the guard is always satisfied. Exactly-once claiming is provided by the write lock the claim
takes plus the `Status = 'Pending'` filter; the lease exists so the claim can be expressed as a
write without changing status, which is what keeps §6.2 authoritative. The guard is defence-in-depth
against a future change that commits between claiming and promoting, and is retained on that basis.

Both zeros are published rather than dropped, because they mean different things and the
distinction is the informative part: one is a correct observation about a redundant guard, the
other was a genuine gap in coverage.

**Why this section exists in its current form.** An earlier revision recorded *"breaking the claim
statement's atomicity → 5 integration tests failed."* That was accurate when measured and became
false when §9.3 was redesigned, because the statement it referred to no longer existed. Nothing
flagged it: documentation evidence has no build step. Re-running the audit replaced a stale number
with an accurate description of the mechanism — and revealed that the guarantee rests on something
other than the clause the prose had credited.

### 12.5 Test-fidelity gap

One consequence of SQLite must be stated rather than glossed over: the `FOR UPDATE SKIP LOCKED`
claim strategy (§9.3) **cannot be exercised by this test suite**, because the engine does not
support it. The SQLite strategy is fully tested; the PostgreSQL strategy is not.

This is why `IPendingOrderClaimer` is defined as a port with a shared contract test suite. The
same behavioural tests — *claims at most `batchSize`*, *never claims twice*, *never claims a
cancelled order* — are written against the interface, so the PostgreSQL adapter can be verified by
running the identical suite against a real PostgreSQL instance if one becomes available. The tests
exist and are provider-agnostic; only the second execution environment is missing.

Being explicit about what is *not* covered is more useful than a coverage percentage that implies
otherwise.

---

## 13. Build, run and delivery

### 13.1 Local development

```bash
dotnet run --project src/OrderProcessing.Api    # starts the API; DB created and seeded on first run
dotnet test                                     # full suite; no Docker, no services required
```

No installation, no container runtime, no connection string to configure. The SQLite database file
(`orders.db`) is created in the application's content root on first run, migrations are applied
automatically, and seed data (a product catalog, two customers and one admin) is inserted in the
`Development` environment so the API can be exercised immediately.

Swagger UI at `/swagger`, with JWT auth wired in so endpoints can be called from the browser.
`POST /dev/token` issues a token for any seeded customer or admin — the fastest path for a reviewer
to authenticate. A `.http` file with ready-made requests covering the full order lifecycle is
included alongside it.

To reset state, delete `orders.db` and restart; the database is rebuilt and reseeded.

### 13.2 Continuous integration

GitHub Actions on every push: restore → build with warnings as errors → unit tests → integration
tests → coverage report. Because there are no service dependencies, the workflow needs no
containers, no service definitions and no secrets, and runs on a stock runner.

### 13.3 Repository layout

```
/docs/SPECIFICATION.md     — this document
/docs/API.md               — HTTP reference: endpoints, schemas, status codes (§11)
/docs/AI-USAGE.md          — required AI-usage log (§14)
/src, /tests               — as per §4
/requests.http             — sample requests covering the full lifecycle
/tools/mutation-audit.ps1  — reproducible mutation audit (§12.4)
/README.md                 — quick start, functional overview, trade-offs, known limitations
```

---

## 14. AI-usage log

The brief explicitly requires the candidate to explain *what AI was used for, what issues were
found, and how they were corrected.* This is a graded deliverable, not an appendix.

`docs/AI-USAGE.md` is maintained **contemporaneously**, with each entry structured as:

1. **Task** — what was asked
2. **Output** — what the assistant produced
3. **Issue** — what was wrong, missing or questionable
4. **Correction** — what was changed, and the reasoning

Writing this as the work happens matters: a log reconstructed at the end tends to read as
invented, whereas a contemporaneous one carries specifics that cannot be fabricated convincingly.

The design phase already produced entries worth recording — notably the cancellation policy, where
the initial proposal granted cancellation rights to customers only, and was corrected to a
role-aware model after the observation that operations staff need an override under defined
circumstances. That correction is what produced the `(role, state) → state` matrix in §6.2.

---

## 15. Decision log

| # | Decision | Alternatives considered | Rationale |
| --- | --- | --- | --- |
| 1 | .NET 10 + ASP.NET Core | .NET 8; Java/Spring Boot; Node/NestJS | Current LTS and the only SDK installed; `TimeProvider` and `BackgroundService` built in |
| 2 | SQLite, zero external dependencies | PostgreSQL in Docker; EF InMemory | Runs with `dotnet run` alone; a real relational engine, unlike InMemory |
| 3 | Money as integer minor units | `decimal`; `numeric(18,2)` | SQLite sorts TEXT decimals lexicographically, breaking FR-4.7; integers are exact and conventional |
| 4 | Catalog + Customer entities | Client-supplied prices | Removes price tampering; enables snapshot semantics |
| 5 | `PATCH /status` + `POST /cancel` | One generic endpoint; per-transition endpoints | Mirrors the brief; cancel differs in auth, precondition and semantics |
| 6 | Role-aware transition matrix | State-only matrix | Models the real need for an admin override |
| 7 | Cancellation capped at `PROCESSING` | Allow cancelling `SHIPPED` | Post-dispatch reversal is a returns flow, not a cancellation |
| 8 | JWT + central query-scoping | ASP.NET Identity; stub headers | Realistic and explainable without registration/password scope creep |
| 9 | `404` not `403` for others' orders | `403` | Avoids existence disclosure and ID enumeration |
| 10 | `IPendingOrderClaimer` port, two adapters | Inline provider-specific SQL | Keeps scheduler and tests provider-agnostic; isolates the one real portability gap |
| 11 | Atomic `UPDATE … RETURNING` claim | `SKIP LOCKED` (unavailable on SQLite) | Correct under SQLite's write serialization; re-asserts state in the `WHERE` clause |
| 12 | Application-managed `Version` token | `xmin`; `rowversion` | Both are server-generated and unavailable on SQLite; guarantee is identical |
| 13 | `BackgroundService` + `PeriodicTimer` | Quartz clustering; Hangfire | Concurrency handling is the substance of the requirement; both add infrastructure |
| 14 | Clean-architecture layering | Single project | Separation of domain rules from infrastructure is an evaluation criterion |
| 15 | Real SQLite in tests | EF Core InMemory provider | InMemory enforces no constraints — tests would pass without proving anything |
| 16 | Shouldly for assertions | FluentAssertions | FluentAssertions 8+ requires a paid licence for commercial use; Shouldly is BSD and equivalent here |
| 17 | Central Package Management | Per-project versions | One source of truth for versions across seven projects; prevents silent drift |

---

## 16. Future work

Deliberately excluded, recorded to show the boundary was chosen rather than overlooked:

- **Migration to PostgreSQL** — the largest single upgrade. Adds true multi-writer concurrency,
  `FOR UPDATE SKIP LOCKED`, native `numeric` and `uuid`, and MVCC. The work is confined to a
  second `IPendingOrderClaimer` adapter, a provider swap and regenerated migrations; the domain,
  application layer, API contract and test suite are unaffected — which is the point of §4
- **Inventory reservation** with expiry, to prevent overselling
- **Payment integration** with an idempotent capture/refund flow
- **Outbox pattern** for reliable `OrderPlaced` / `OrderCancelled` event publication
- **Notifications** driven off those events
- **Read/write separation** should list-query volume outgrow the primary
- **Order archival** for orders beyond a retention threshold
- **Multi-currency**, building on the `Money` value object introduced in §5.4
