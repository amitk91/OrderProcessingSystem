# API Reference

Complete HTTP contract for the Order Processing System.

- **Base path:** `/api/v1`
- **Content type:** `application/json`
- **Interactive docs:** <http://localhost:5000/swagger> when running
- **Executable examples:** [`../requests.http`](../requests.http)

For *why* the API behaves this way, see [SPECIFICATION.md](./SPECIFICATION.md).
For a functional overview, see the [README](../README.md).

---

## Contents

1. [Authentication](#1-authentication)
2. [Endpoint summary](#2-endpoint-summary)
3. [Orders](#3-orders)
4. [Products](#4-products)
5. [Health](#5-health)
6. [Development token](#6-development-token)
7. [Errors](#7-errors)
8. [Data types](#8-data-types)

---

## 1. Authentication

All endpoints require a JWT bearer token except health probes and the development token endpoint.

```http
Authorization: Bearer <token>
```

The token carries two claims that matter:

| Claim | Meaning |
| --- | --- |
| `sub` | The caller's user id. **This is the only source of caller identity** — a `customerId` in a request body or query string is never trusted. |
| `role` | `Customer` or `Admin`. |

### Access rules

| Role | Can do |
| --- | --- |
| `Customer` | Create orders; read, list and cancel **only their own** orders |
| `Admin` | Read and list **any** order; transition status; cancel from `PENDING` or `PROCESSING` |

**Requesting another customer's order returns `404`, not `403`.** A `403` would confirm the order
exists and allow id enumeration. Where the *operation* is forbidden regardless of ownership — a
customer calling `PATCH /status` — the response is `403`, because that discloses nothing.

### Obtaining a token (development only)

```bash
curl -X POST http://localhost:5000/api/v1/dev/token \
  -H "Content-Type: application/json" \
  -d '{"userId":"11111111-1111-1111-1111-111111111111","role":"Customer","email":"alice@example.com"}'
```

---

## 2. Endpoint summary

| Method | Path | Auth | Purpose |
| --- | --- | --- | --- |
| `POST` | `/api/v1/orders` | Customer | [Create an order](#31-create-an-order) |
| `GET` | `/api/v1/orders/{id}` | Owner / Admin | [Retrieve an order](#32-retrieve-an-order) |
| `GET` | `/api/v1/orders` | Owner / Admin | [List orders](#33-list-orders) |
| `PATCH` | `/api/v1/orders/{id}/status` | **Admin** | [Transition status](#34-update-order-status) |
| `POST` | `/api/v1/orders/{id}/cancel` | Owner / Admin | [Cancel an order](#35-cancel-an-order) |
| `GET` | `/api/v1/products` | Any | [List products](#4-products) |
| `GET` | `/health/live` | Anonymous | [Liveness](#5-health) |
| `GET` | `/health/ready` | Anonymous | [Readiness](#5-health) |
| `POST` | `/api/v1/dev/token` | Anonymous | [Issue a token](#6-development-token) — Development only |

---

## 3. Orders

### 3.1 Create an order

```http
POST /api/v1/orders
```

Places an order for the authenticated customer. The order starts in `PENDING`.

**Headers**

| Header | Required | Description |
| --- | --- | --- |
| `Authorization` | Yes | `Bearer <token>` |
| `Idempotency-Key` | No | Up to 64 characters. Repeating a request with the same key returns the original order instead of creating a duplicate. Scoped per customer. |

> **Safe under concurrency, not just on retry.** The guarantee holds when several requests carrying
> the same key are in flight simultaneously: exactly one order is created, one request receives
> `201` and the rest receive `200` with that same order. This is enforced by a unique index rather
> than by a pre-insert check, so it cannot be defeated by timing.

**Request body**

| Field | Type | Rules |
| --- | --- | --- |
| `items` | array | At least one element |
| `items[].productId` | uuid | Must reference an existing, active product |
| `items[].quantity` | integer | 1 – 1000 |

> **No price field.** Unit prices are resolved server-side from the catalogue. Any price included
> in the request body is ignored, so a caller cannot influence what it is charged.

```json
{
  "items": [
    { "productId": "aaaaaaaa-0000-0000-0000-000000000001", "quantity": 2 },
    { "productId": "aaaaaaaa-0000-0000-0000-000000000002", "quantity": 1 }
  ]
}
```

**Behaviour**

- Duplicate `productId` values in one request are **merged**, and their quantities summed
- `lineTotal` = `unitPrice` × `quantity`; `totalAmount` = sum of all line totals
- A unique `orderNumber` is assigned, for example `ORD-2026-000042`
- An initial status-history entry (`null → PENDING`) is written

**Responses**

| Status | When |
| --- | --- |
| `201 Created` | Order created. `Location` header points to the new order. |
| `200 OK` | An `Idempotency-Key` matched an existing order; the original is returned unchanged. |
| `400 Bad Request` | Validation failure (no items, quantity out of range) or an unknown `productId` |
| `401 Unauthorized` | Missing or invalid token |
| `422 Unprocessable Entity` | Product is inactive, or the idempotency key was reused with a different payload |

```http
HTTP/1.1 201 Created
Location: /api/v1/orders/01a0d8cf-df40-7ef1-893d-3cee9ea29ad0
```

```json
{
  "id": "01a0d8cf-df40-7ef1-893d-3cee9ea29ad0",
  "orderNumber": "ORD-2026-000001",
  "customerId": "11111111-1111-1111-1111-111111111111",
  "status": "PENDING",
  "currency": "USD",
  "totalAmount": 119.97,
  "items": [
    {
      "productId": "aaaaaaaa-0000-0000-0000-000000000001",
      "productName": "Mechanical Keyboard",
      "unitPrice": 49.99,
      "quantity": 2,
      "lineTotal": 99.98
    },
    {
      "productId": "aaaaaaaa-0000-0000-0000-000000000002",
      "productName": "Wireless Mouse",
      "unitPrice": 19.99,
      "quantity": 1,
      "lineTotal": 19.99
    }
  ],
  "createdAt": "2026-09-25T13:30:00.0000000+00:00",
  "updatedAt": "2026-09-25T13:30:00.0000000+00:00",
  "cancelledAt": null,
  "cancelledBy": null,
  "cancellationReason": null,
  "statusHistory": [
    {
      "fromStatus": null,
      "toStatus": "PENDING",
      "changedBy": "CUSTOMER",
      "reason": null,
      "changedAt": "2026-09-25T13:30:00.0000000+00:00"
    }
  ]
}
```

---

### 3.2 Retrieve an order

```http
GET /api/v1/orders/{id}
```

Returns one order with its items and full status history.

| Status | When |
| --- | --- |
| `200 OK` | Order returned |
| `401 Unauthorized` | Missing or invalid token |
| `404 Not Found` | Order does not exist **or belongs to another customer** |

---

### 3.3 List orders

```http
GET /api/v1/orders
```

Returns a page of orders. Customers see only their own; admins see everything.

**Query parameters**

| Parameter | Type | Default | Notes |
| --- | --- | --- | --- |
| `status` | string | – | `PENDING`, `PROCESSING`, `SHIPPED`, `DELIVERED`, `CANCELLED`. Case-insensitive. |
| `customerId` | uuid | – | **Admins only.** Ignored for customers, who cannot widen their scope. |
| `page` | integer | `1` | Values below 1 are treated as 1. |
| `pageSize` | integer | `20` | Maximum 100. Larger values are **clamped, not rejected**. Values ≤ 0 use the default. |
| `sortBy` | string | `createdAt` | `createdAt` or `totalAmount`. |
| `desc` | boolean | `true` | Sort descending. |

**Responses**

| Status | When |
| --- | --- |
| `200 OK` | Page returned. An empty result is an empty array, not a `404`. |
| `400 Bad Request` | Unrecognised `status` or an unsortable `sortBy` |
| `401 Unauthorized` | Missing or invalid token |

```json
{
  "items": [ /* OrderDto objects */ ],
  "page": 1,
  "pageSize": 20,
  "totalCount": 42,
  "totalPages": 3
}
```

> Sorting by `totalAmount` is numeric. Totals are stored as integer minor units, so `9.99` correctly
> precedes `100.00` — see [§5.4 of the specification](./SPECIFICATION.md) for why this matters.

---

### 3.4 Update order status

```http
PATCH /api/v1/orders/{id}/status
```

**Administrators only.** Moves an order through the fulfilment lifecycle.

**Request body**

| Field | Type | Rules |
| --- | --- | --- |
| `status` | string | Target status |
| `reason` | string | Optional, up to 500 characters. Required when the target is `CANCELLED`. |

```json
{ "status": "PROCESSING" }
```

**Permitted transitions**

| From → To | `PROCESSING` | `SHIPPED` | `DELIVERED` | `CANCELLED` |
| --- | --- | --- | --- | --- |
| **`PENDING`** | Admin, System | ✗ | ✗ | Customer, Admin |
| **`PROCESSING`** | ✗ | Admin | ✗ | Admin only |
| **`SHIPPED`** | ✗ | ✗ | Admin | ✗ |
| **`DELIVERED`** | ✗ | ✗ | ✗ | ✗ terminal |
| **`CANCELLED`** | ✗ | ✗ | ✗ | ✗ terminal |

Skip transitions (`PENDING → SHIPPED`) and backward transitions are rejected.

**Responses**

| Status | When |
| --- | --- |
| `200 OK` | Transition applied, **or** the order was already in the requested status (idempotent no-op) |
| `400 Bad Request` | Unrecognised status value |
| `403 Forbidden` | Caller is not an administrator |
| `404 Not Found` | Order does not exist |
| `409 Conflict` | Transition not permitted from the current state |

A `409` includes the transitions that *were* available, so the error is actionable:

```json
{
  "type": "https://orderprocessing/errors/invalid-status-transition",
  "title": "Invalid status transition",
  "status": 409,
  "detail": "Cannot transition order from Pending to Delivered as Admin; permitted transitions are: Processing, Cancelled.",
  "instance": "/api/v1/orders/01a0d8cf.../status",
  "attemptedTransition": "Pending -> Delivered",
  "permittedTransitions": ["PROCESSING", "CANCELLED"],
  "correlationId": "0HNOR1BMF04TF"
}
```

---

### 3.5 Cancel an order

```http
POST /api/v1/orders/{id}/cancel
```

**Request body**

| Field | Type | Rules |
| --- | --- | --- |
| `reason` | string | Up to 500 characters. **Mandatory for administrators**, optional for customers. |

```json
{ "reason": "Payment failed on capture" }
```

**Who may cancel, and from where**

| Actor | `PENDING` | `PROCESSING` | `SHIPPED` | `DELIVERED` | `CANCELLED` |
| --- | --- | --- | --- | --- | --- |
| **Customer** | ✅ `200` | ❌ `409` | ❌ `409` | ❌ `409` | ⟳ `200` no-op |
| **Admin** | ✅ `200` | ✅ `200` reason required | ❌ `409` | ❌ `409` | ⟳ `200` no-op |

Past `SHIPPED` an order cannot be cancelled: reversing a dispatched order is a returns flow with
its own restocking and refund semantics, which is out of scope.

**Responses**

| Status | When |
| --- | --- |
| `200 OK` | Cancelled, or already cancelled (idempotent no-op) |
| `400 Bad Request` | Administrator supplied no reason |
| `401 Unauthorized` | Missing or invalid token |
| `404 Not Found` | Order does not exist or belongs to another customer |
| `409 Conflict` | Not cancellable from the current state for this caller |

On success the response records `cancelledAt`, `cancelledBy` and `cancellationReason`, and appends
a status-history entry. **Re-cancelling never overwrites the original audit data.**

---

## 4. Products

```http
GET /api/v1/products
```

Read-only catalogue access, so a client can discover product ids before ordering.

| Parameter | Type | Default | Notes |
| --- | --- | --- | --- |
| `includeInactive` | boolean | `false` | Include products that can no longer be ordered |

```json
[
  {
    "id": "aaaaaaaa-0000-0000-0000-000000000001",
    "sku": "KB-001",
    "name": "Mechanical Keyboard",
    "unitPrice": 49.99,
    "currency": "USD",
    "isActive": true
  }
]
```

---

## 5. Health

| Endpoint | Purpose |
| --- | --- |
| `GET /health/live` | Liveness. Returns `200 Healthy` if the process is running. |
| `GET /health/ready` | Readiness. Includes a database probe; returns `503` if the database is unreachable. |

Both are anonymous. Liveness deliberately runs no dependency checks, so a failing database takes
an instance out of rotation without triggering a restart loop.

---

## 6. Development token

```http
POST /api/v1/dev/token
```

> **Development environment only.** This is not an authentication system — it issues a token for
> any user id it is given, with no credential check. It exists so the API can be exercised from
> Swagger without a full identity provider.

**Request**

| Field | Type | Notes |
| --- | --- | --- |
| `userId` | uuid | Becomes the `sub` claim |
| `role` | string | `Customer` or `Admin`. Anything else is treated as `Customer`. |
| `email` | string | Optional |

**Response**

```json
{
  "token": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...",
  "userId": "11111111-1111-1111-1111-111111111111",
  "role": "Customer"
}
```

---

## 7. Errors

Errors come in two shapes.

### 7.1 Validation failures

Requests rejected by model validation — a missing field, an empty `items` array, a quantity out of
range — return `400` with a per-field `errors` dictionary:

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": {
    "Items[0].Quantity": ["Quantity must be between 1 and 1000."]
  },
  "correlationId": "0HNOR28EE8OJU"
}
```

These are caught before the request reaches any business logic, so the `errors` dictionary is the
useful part rather than the `type`.

### 7.2 Business-rule failures

Everything else is an RFC 7807 `ProblemDetails` whose `type` carries a stable, machine-readable
code in its final segment. Branch on that code rather than parsing `detail`.

```json
{
  "type": "https://orderprocessing/errors/product-inactive",
  "title": "Product unavailable",
  "status": 422,
  "detail": "Product 'Discontinued Webcam' is no longer available.",
  "instance": "/api/v1/orders",
  "correlationId": "0HNOR1BMF04TF"
}
```

Every response of both kinds carries a `correlationId` matching the server logs.

### 7.3 Error codes

| Code | Status | Meaning |
| --- | --- | --- |
| `product-not-found` | `400` | A referenced product does not exist |
| `cancellation-reason-required` | `400` | Administrator cancelled without a reason |
| `empty-order` | `400` | Order contained no items after merging. Normally caught earlier by validation (§7.1). |
| `order-not-found` | `404` | Order does not exist, **or is not visible to the caller** |
| `invalid-status-transition` | `409` | Transition not permitted from the current state |
| `concurrency-conflict` | `409` | The order changed during the request; retry |
| `product-inactive` | `422` | Product exists but cannot be ordered |
| `idempotency-key-conflict` | `422` | Key reused with a different payload |
| `write-conflict` | `409` | Two requests contended for the same unique value; retry |
| `internal-error` | `500` | Unexpected failure. No internal detail is disclosed. |

An `invalid-status-transition` adds two fields so the error is actionable:

| Field | Example |
| --- | --- |
| `attemptedTransition` | `"Pending -> Delivered"` |
| `permittedTransitions` | `["PROCESSING", "CANCELLED"]` |

### 7.4 Status code conventions

| Status | Used when |
| --- | --- |
| `200` | Successful read, or an accepted idempotent no-op |
| `201` | Order created |
| `400` | Malformed request or validation failure |
| `401` | Missing or invalid token |
| `403` | Operation forbidden for the caller's **role** |
| `404` | Not found, **or not owned by the caller** |
| `409` | Illegal state transition, or a concurrency conflict |
| `422` | Well-formed but semantically invalid |

The distinction between `400` and `422` is deliberate: `400` means the request itself was wrong,
`422` means the request was well-formed but the system cannot act on it.

---

## 8. Data types

### Order

| Field | Type | Notes |
| --- | --- | --- |
| `id` | uuid | |
| `orderNumber` | string | Human-readable, unique, for example `ORD-2026-000042` |
| `customerId` | uuid | Always the authenticated caller on creation |
| `status` | string | `PENDING` \| `PROCESSING` \| `SHIPPED` \| `DELIVERED` \| `CANCELLED` |
| `currency` | string | ISO 4217, for example `USD` |
| `totalAmount` | decimal | Computed server-side; never accepted from the client |
| `items` | array | See [Order item](#order-item) |
| `createdAt` | timestamp | ISO-8601 with offset, UTC |
| `updatedAt` | timestamp | Time of the most recent change |
| `cancelledAt` | timestamp? | Null unless cancelled |
| `cancelledBy` | string? | `CUSTOMER` \| `ADMIN` \| `SYSTEM` |
| `cancellationReason` | string? | Present for administrator cancellations |
| `statusHistory` | array | See [Status history entry](#status-history-entry), oldest first |

### Order item

| Field | Type | Notes |
| --- | --- | --- |
| `productId` | uuid | |
| `productName` | string | **Snapshot** taken at order time |
| `unitPrice` | decimal | **Snapshot** taken at order time |
| `quantity` | integer | |
| `lineTotal` | decimal | `unitPrice` × `quantity` |

> Snapshots mean a later catalogue price change does not alter historical orders.

### Status history entry

| Field | Type | Notes |
| --- | --- | --- |
| `fromStatus` | string? | Null for the entry written at creation |
| `toStatus` | string | |
| `changedBy` | string | `CUSTOMER` \| `ADMIN` \| `SYSTEM` |
| `reason` | string? | |
| `changedAt` | timestamp | |

### Paged result

| Field | Type | Notes |
| --- | --- | --- |
| `items` | array | The page of results |
| `page` | integer | 1-based |
| `pageSize` | integer | Effective size after clamping |
| `totalCount` | integer | Total matching records, not just this page |
| `totalPages` | integer | Derived from `totalCount` and `pageSize` |

### Product

| Field | Type | Notes |
| --- | --- | --- |
| `id` | uuid | |
| `sku` | string | Unique |
| `name` | string | |
| `unitPrice` | decimal | Authoritative catalogue price |
| `currency` | string | ISO 4217 |
| `isActive` | boolean | Inactive products cannot be ordered |

---

## Background status promotion

Not an endpoint, but it changes order state and is therefore visible through this API.

A background job runs every 5 minutes (configurable) and promotes `PENDING` orders to `PROCESSING`.
Promotions appear in `statusHistory` with `changedBy: "SYSTEM"` and no user attributed. Orders
cancelled before a run are never promoted.

To observe it without waiting five minutes:

```bash
OrderPromotion__Interval=00:00:05 dotnet run --project src/OrderProcessing.Api
```
