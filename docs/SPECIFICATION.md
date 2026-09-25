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
| Runtime | .NET 8 (LTS) | Long-term support; built-in `TimeProvider` and `BackgroundService` |
| API | ASP.NET Core Web API | Standard, well-understood, first-class OpenAPI support |
| Persistence | PostgreSQL 16 + EF Core 8 (Npgsql) | See §3.1 |
| Migrations | EF Core Migrations | Version-controlled, reviewable schema changes |
| Validation | FluentValidation | Keeps validation rules out of controllers and independently testable |
| Auth | JWT bearer (`Microsoft.AspNetCore.Authentication.JwtBearer`) | Stateless, role claims, standard tooling |
| Testing | xUnit, FluentAssertions, Testcontainers, WebApplicationFactory | Real database in tests; no in-memory provider fiction |
| Containerization | Docker + docker-compose | Single-command startup for a reviewer |
| CI | GitHub Actions | Build, test and report on every push |

### 3.1 Why PostgreSQL

The order domain is transactional and money-bearing, so a relational ACID store is the correct
choice; eventual consistency is not acceptable for whether a customer was charged. Between the
relational options, PostgreSQL was chosen because:

1. **`FOR UPDATE SKIP LOCKED`** is exactly the primitive the background job needs to claim
   batches safely across multiple instances (§9.3).
2. **No licensing friction** — Testcontainers-based integration tests run on any machine or CI runner.
3. **MVCC** gives good reader/writer concurrency; readers do not block writers.
4. **JSONB** is available for flexible product attributes without leaving the relational model.

SQL Server would be equally capable — the design is unchanged, substituting the `READPAST` hint
for `SKIP LOCKED`. The choice is driven by ecosystem and licensing rather than capability.

> A production e-commerce platform would be polyglot: relational for orders and payments,
> Elasticsearch for product search, Redis for carts and sessions, and a warehouse for analytics.
> This service owns the **orders** bounded context, which is squarely relational.

---

## 4. Architecture

Clean-architecture layering with dependencies pointing inward. This is more structure than five
endpoints strictly require; it is adopted because the separation of domain rules from
infrastructure is a primary evaluation criterion.

```
src/
  OrderProcessing.Domain/          — entities, value objects, state machine, domain errors
                                     (no external dependencies)
  OrderProcessing.Application/     — use cases, DTOs, port interfaces, validators
  OrderProcessing.Infrastructure/  — EF Core, repositories, auth, background job
  OrderProcessing.Api/             — controllers, middleware, composition root
tests/
  OrderProcessing.Domain.Tests/        — fast, no I/O
  OrderProcessing.Application.Tests/   — use cases against test doubles
  OrderProcessing.Integration.Tests/   — full stack against Testcontainers PostgreSQL
```

**Dependency rule:** `Api → Infrastructure → Application → Domain`. The domain references nothing.

**Transaction boundary:** one transaction per use case, opened in the application layer via a
unit-of-work abstraction. The background job uses one transaction *per batch* (§9.4).

---

## 5. Domain model

### 5.1 Entities

**Customer**

| Field | Type | Notes |
| --- | --- | --- |
| `Id` | `uuid` | PK |
| `Email` | `varchar(256)` | Unique, case-insensitive |
| `FullName` | `varchar(200)` | |
| `CreatedAt` | `timestamptz` | |

**Product**

| Field | Type | Notes |
| --- | --- | --- |
| `Id` | `uuid` | PK |
| `Sku` | `varchar(64)` | Unique |
| `Name` | `varchar(200)` | |
| `UnitPrice` | `numeric(18,2)` | Authoritative price; never accepted from the client |
| `IsActive` | `boolean` | Inactive products cannot be ordered |

**Order**

| Field | Type | Notes |
| --- | --- | --- |
| `Id` | `uuid` | PK |
| `OrderNumber` | `varchar(20)` | Human-readable, unique (e.g. `ORD-2026-000042`) |
| `CustomerId` | `uuid` | FK → Customer, indexed |
| `Status` | `varchar(16)` | Enum, indexed |
| `TotalAmount` | `numeric(18,2)` | Always computed server-side |
| `Currency` | `char(3)` | ISO 4217 |
| `IdempotencyKey` | `varchar(64)` | Nullable, unique per customer |
| `CreatedAt` / `UpdatedAt` | `timestamptz` | |
| `CancelledAt` | `timestamptz` | Nullable |
| `CancelledBy` | `varchar(16)` | Nullable — `Customer` / `Admin` / `System` |
| `CancellationReason` | `varchar(500)` | Nullable; mandatory for admin cancellations |
| `Version` | `xmin` (concurrency token) | Optimistic concurrency (§10.2) |

**OrderItem**

| Field | Type | Notes |
| --- | --- | --- |
| `Id` | `uuid` | PK |
| `OrderId` | `uuid` | FK → Order, indexed, cascade delete |
| `ProductId` | `uuid` | FK → Product |
| `ProductName` | `varchar(200)` | **Snapshot** at order time |
| `UnitPrice` | `numeric(18,2)` | **Snapshot** at order time |
| `Quantity` | `int` | `> 0` |
| `LineTotal` | `numeric(18,2)` | `UnitPrice × Quantity`, persisted for auditability |

**OrderStatusHistory**

| Field | Type | Notes |
| --- | --- | --- |
| `Id` | `uuid` | PK |
| `OrderId` | `uuid` | FK → Order, indexed |
| `FromStatus` / `ToStatus` | `varchar(16)` | `FromStatus` null on creation |
| `ChangedBy` | `varchar(16)` | `Customer` / `Admin` / `System` |
| `ChangedByUserId` | `uuid` | Nullable — null for system transitions |
| `Reason` | `varchar(500)` | Nullable |
| `ChangedAt` | `timestamptz` | |

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
| `IX_Orders_IdempotencyKey` (unique, filtered) | Idempotent creation lookup |
| `IX_OrderItems_OrderId` | Loading items for an order |
| `IX_OrderStatusHistory_OrderId_ChangedAt` | Rendering an order's audit trail |

### 5.4 Money handling

`numeric(18,2)` in PostgreSQL, mapped to `decimal` in .NET. Floating-point types are never used
for money. Totals are always recomputed server-side from the persisted line items and never
trusted from client input.

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
| **Customer** | ✅ Allowed | ❌ `409` — already being fulfilled | ❌ `409` | ❌ `409` | ❌ `409` |
| **Admin** | ✅ Allowed | ✅ Allowed (reason required) | ❌ `409` — returns flow | ❌ `409` | ❌ `409` |

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
| FR-1.6 | Any client-supplied price is **ignored**, never trusted |
| FR-1.7 | `LineTotal = UnitPrice × Quantity`; `TotalAmount = Σ LineTotal` |
| FR-1.8 | Duplicate product IDs in one request are merged, summing their quantities |
| FR-1.9 | New orders are created as `PENDING` |
| FR-1.10 | A unique, human-readable `OrderNumber` is assigned |
| FR-1.11 | An initial `OrderStatusHistory` row (`null → PENDING`) is written |
| FR-1.12 | Repeating a request with the same `Idempotency-Key` returns the original order, not a duplicate |
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
| FR-3.5 | Transitioning to the same status is a no-op returning `200` |
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
| FR-5.7 | Cancelling an already-cancelled order returns `409` and does not overwrite the original audit data |

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
library would obscure the reasoning. Quartz with clustering would be the production choice once
richer scheduling (cron expressions, misfire policies, a dashboard) is needed.

### 9.2 Testability

The job depends on `TimeProvider` (built into .NET 8) rather than `DateTime.UtcNow`, and the
timer is abstracted behind an interface. Tests use a fake time provider to advance the clock
deterministically — a 5-minute schedule is verified in milliseconds, with no `Thread.Sleep`.

### 9.3 Multi-instance safety

Claiming uses `SELECT ... FOR UPDATE SKIP LOCKED`:

```sql
SELECT id FROM orders
WHERE status = 'PENDING'
ORDER BY created_at
LIMIT @batchSize
FOR UPDATE SKIP LOCKED;
```

Each instance locks a disjoint set of rows; `SKIP LOCKED` means a second instance steps over rows
already claimed rather than blocking. Two instances therefore make progress in parallel without
ever double-processing an order — satisfying FR-6.4 without a distributed lock or leader election.

### 9.4 Execution model

- **Bounded batches** (default 100, configurable) loop until no `PENDING` orders remain or a
  per-run cap is reached, bounding memory and transaction duration
- **One transaction per batch**, so a large backlog does not hold a single long transaction
- **Failure isolation** — a per-order failure is logged and skipped; the rest of the batch commits
- **Non-overlapping runs** — a run exceeding the interval is not re-entered; the next tick is skipped
- **Status re-checked inside the transaction**, so an order cancelled between claim and commit is
  not promoted (FR-6.6)
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

An optimistic concurrency token on `Order` (PostgreSQL `xmin`) makes lost updates impossible.
The specific race worth calling out: **an admin cancels an order at the same moment the scheduler
promotes it from `PENDING` to `PROCESSING`.** One transaction commits; the other detects the
version change, reloads and re-evaluates against the new state. The result is deterministic
rather than dependent on timing, and it is covered by an explicit integration test.

Concurrency conflicts surface as `409 Conflict` with a retry hint.

### 10.3 Reliability and availability

Stateless API instances, horizontally scalable behind a load balancer. Liveness
(`/health/live`) and readiness (`/health/ready`, including a database probe) endpoints. Graceful
shutdown drains in-flight requests. Connection pooling with a bounded pool; transient database
faults retried with exponential backoff via EF Core's execution strategy.

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
| **Unit — domain** | State machine, totals, invariants | xUnit, FluentAssertions |
| **Unit — application** | Use-case orchestration | xUnit, NSubstitute |
| **Integration — API** | Full stack against real PostgreSQL | `WebApplicationFactory` + Testcontainers |
| **Integration — scheduler** | Claiming, batching, concurrency | Testcontainers + fake `TimeProvider` |

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
5. **Multi-instance scheduler** — two scheduler instances over a shared backlog promote every
   order exactly once
6. **Idempotent creation** — the same `Idempotency-Key` twice yields one order
7. **Price tampering** — a client-supplied `unitPrice` in the create payload is ignored
8. **Price-snapshot integrity** — changing a catalog price does not alter an existing order's total
9. **Batch failure isolation** — one poison order does not prevent the rest of the batch committing
10. **Page-size clamping** — `?pageSize=10000` is clamped to the maximum rather than rejected

### 12.3 Approach

Real PostgreSQL via Testcontainers throughout — the in-memory provider does not enforce
constraints or reproduce locking semantics, so tests against it can pass while production fails.
Test data is built with the builder pattern to keep intent legible. Tests are independent and
parallelizable, with per-test database isolation. No `Thread.Sleep` anywhere; time is always
controlled via `TimeProvider`.

---

## 13. Build, run and delivery

### 13.1 Local development

```bash
docker compose up          # API + PostgreSQL, migrations applied on startup
dotnet test                # full suite (Testcontainers requires a running Docker daemon)
```

Swagger UI at `/swagger`, with JWT auth configured so endpoints can be exercised from the browser.
Seed data (products, two customers, an admin) is applied in `Development` so a reviewer can
exercise the API immediately.

### 13.2 Continuous integration

GitHub Actions on every push: restore → build with warnings as errors → unit tests → integration
tests (Testcontainers) → coverage report.

### 13.3 Repository layout

```
/docs/SPECIFICATION.md     — this document
/docs/AI-USAGE.md          — required AI-usage log (§14)
/src, /tests               — as per §4
/docker-compose.yml
/README.md                 — quick start, design decisions, trade-offs, known limitations
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
| 1 | .NET 8 + ASP.NET Core | Java/Spring Boot, Node/NestJS | Team fit; `TimeProvider` and `BackgroundService` built in |
| 2 | PostgreSQL | SQL Server, SQLite, in-memory | `SKIP LOCKED`; no licensing friction in CI; MVCC |
| 3 | Catalog + Customer entities | Client-supplied prices | Removes price tampering; enables snapshot semantics |
| 4 | `PATCH /status` + `POST /cancel` | One generic endpoint; per-transition endpoints | Mirrors the brief; cancel differs in auth, precondition and semantics |
| 5 | Role-aware transition matrix | State-only matrix | Models the real need for an admin override |
| 6 | Cancellation capped at `PROCESSING` | Allow cancelling `SHIPPED` | Post-dispatch reversal is a returns flow, not a cancellation |
| 7 | JWT + central query-scoping | ASP.NET Identity; stub headers | Realistic and explainable without registration/password scope creep |
| 8 | `404` not `403` for others' orders | `403` | Avoids existence disclosure and ID enumeration |
| 9 | `BackgroundService` + `SKIP LOCKED` | Quartz clustering; Hangfire | Concurrency handling is the substance of the requirement |
| 10 | Optimistic concurrency via `xmin` | Pessimistic locking | Non-blocking; conflicts are rare and safely retried |
| 11 | Clean-architecture layering | Single project | Separation of domain rules from infrastructure is an evaluation criterion |
| 12 | Testcontainers | In-memory EF provider | In-memory does not enforce constraints or reproduce locking |

---

## 16. Future work

Deliberately excluded, recorded to show the boundary was chosen rather than overlooked:

- **Inventory reservation** with expiry, to prevent overselling
- **Payment integration** with an idempotent capture/refund flow
- **Outbox pattern** for reliable `OrderPlaced` / `OrderCancelled` event publication
- **Notifications** driven off those events
- **Read/write separation** should list-query volume outgrow the primary
- **Order archival** for orders beyond a retention threshold
- **Multi-currency**, building on the existing money abstraction
