# AI Usage Log

The assignment requires an explanation of what AI tooling was used for, what issues surfaced, and
how they were corrected. This log is written **as the work happens** rather than reconstructed at
the end, so the entries carry the actual specifics of each exchange.

**Tooling used:** GitHub Copilot (agent mode, VS Code).

Each entry follows the same structure: what was asked → what the assistant produced → what was
wrong or missing → what was changed and why.

---

## Phase 1 — Design and requirements

### Entry 1 — Turning a high-level brief into a specification

**Task.** The brief ([Func-Req.md](../Func-Req.md)) is five bullet points. I asked the assistant
to produce a plan for finalizing functional and non-functional requirements *before* any code was
written, rather than jumping straight to implementation.

**Output.** A twelve-step plan covering stack selection, requirement elaboration, state-machine
formalization, domain modelling, API contract, background-job design, NFRs, architecture, testing,
delivery and the AI-usage log itself.

**Issue.** The plan was sound but risked over-expanding the design phase for a take-home
assignment — the deliverable is a *working, well-tested system*, not an exhaustive specification
document. The assistant flagged this risk itself and recommended time-boxing the phase.

**Correction.** Accepted the plan but drove the decisions to closure quickly rather than
exploring every branch, keeping the specification tight enough to stay consistent with the code
that will actually be shipped.

---

### Entry 2 — Cancellation policy: the correction that shaped the domain model

**Task.** Deciding how order status transitions should be exposed in the API.

**Output.** The assistant proposed `PATCH /orders/{id}/status` for operations-driven transitions
plus `POST /orders/{id}/cancel` for the customer action, arguing that cancellation differs from
other transitions in authorization, precondition and semantics.

**Issue.** The proposal mirrored the brief too literally. The brief states only that *"customers
should be able to cancel an order, but only if it's still in PENDING status"*, and the assistant
designed cancellation as a customer-only capability. That is unrealistic: operations staff
routinely need to cancel an in-flight order for fraud review, a late payment failure, a support
call, or a stock discrepancy.

**Correction.** I raised the admin case. The model was reworked so that the transition guard is
not a simple `from-state → to-state` table but a **`(actor role, from-state) → to-state`** matrix.
Customers retain the exact rule in the brief (`PENDING` only); admins may additionally cancel from
`PROCESSING`, with a mandatory reason recorded for auditability.

A follow-up question — whether admins should also be able to cancel a `SHIPPED` order — was
resolved deliberately as *no*: once goods are in transit the correct process is a return/refund
with its own reversal and restocking semantics, and modelling that as a "cancel" would
misrepresent the business process.

**Why this entry matters.** This is the clearest example in the project of AI output being a
reasonable starting point but not a finished answer. The assistant optimized for fidelity to the
written brief; the correction came from domain knowledge the brief did not contain. The role-aware
transition matrix in §6.2 of the specification is a direct result.

---

### Entry 3 — Security: separating role authorization from ownership

**Task.** I asked how authentication should be handled, noting that I needed to be able to explain
how a customer is prevented from seeing other customers' data.

**Output.** The assistant recommended JWT bearer tokens with role claims, and — importantly —
pointed out that my question conflated two distinct concerns:

- **Role authorization** — *may this kind of actor perform this operation?*
- **Resource-level (ownership) authorization** — *may this specific actor touch this specific row?*

It identified that passing the first while failing the second is the IDOR / Broken Object-Level
Authorization defect, the top item on the OWASP API Security Top 10.

**Issue.** No correction was needed here — this exchange improved my framing rather than the
reverse. The distinction is one I would have blurred in an interview.

**Correction / adoption.** Adopted in full, with two implementation rules worth recording:

1. **Scope the query rather than fetch-then-compare.** Ownership is pushed into the SQL predicate
   (`WHERE Id = @id AND CustomerId = @callerId`) instead of loading the row and checking
   afterwards. Fetch-then-compare works until a new endpoint omits the check; query-scoping is
   fail-closed by construction.
2. **Return `404` rather than `403`** for another customer's order, since `403` confirms the
   resource exists and enables ID enumeration.

---

### Entry 4 — Database selection

**Task.** Rather than accepting a recommendation, I asked which database is generally most
suitable for e-commerce.

**Output.** The assistant distinguished between the *order/payment* context — which requires a
relational ACID store, since "eventually consistent" is unacceptable for whether a customer was
charged — and the wider polyglot reality of e-commerce platforms (Elasticsearch for search, Redis
for carts, a warehouse for analytics). It noted that PostgreSQL versus SQL Server is largely an
ecosystem and licensing decision rather than a capability one.

**Issue.** None; the reasoning held up. The useful detail was that `FOR UPDATE SKIP LOCKED` is
precisely the primitive the background job needs, which made PostgreSQL the better fit for *this*
assignment specifically rather than in the abstract.

**Correction / adoption.** PostgreSQL selected, with the specification explicitly noting that the
design is unchanged under SQL Server apart from substituting the `READPAST` hint — so the choice
does not read as arbitrary.

---

### Entry 5 — Background job: surfacing the hidden difficulty

**Task.** Designing the "update PENDING orders to PROCESSING every 5 minutes" requirement.

**Output.** The assistant reframed the one-line requirement as five questions: multi-instance
double-processing, unbounded loading of PENDING orders, batch-wide rollback on a single failure,
timer drift and self-overlap, and how to test a 5-minute timer without waiting five minutes. It
recommended `BackgroundService` + `PeriodicTimer` with `SELECT ... FOR UPDATE SKIP LOCKED` batch
claiming, over Quartz.NET or Hangfire.

**Issue.** None substantive. The trade-off was explicit and worth recording: Quartz with
clustering would handle multi-instance coordination for free, but delegating to a library would
hide the concurrency reasoning that *is* the substance of this requirement.

**Correction / adoption.** Adopted, with the addition of `TimeProvider` (built into .NET 8) rather
than `DateTime.UtcNow` so the schedule is deterministically testable in milliseconds with no
`Thread.Sleep`. Quartz is recorded in the specification as the production choice once richer
scheduling is needed, so the decision reads as considered rather than uninformed.

---

## Observations so far

- The assistant is strongest at **breadth** — surfacing concerns I had not asked about
  (idempotency keys, `404`-versus-`403`, price snapshots, timer self-overlap).
- It is weakest at **domain realism**. It optimizes for fidelity to the written brief, which is
  exactly why the cancellation policy needed correcting: nothing in the brief mentioned admin
  overrides, so nothing in the output did either.
- Its default posture is to **add scope**. Recommending against ASP.NET Identity and against
  cancelling `SHIPPED` orders both required a deliberate prompt toward simplicity.
- The most valuable exchanges were the ones where I **questioned a recommendation** rather than
  accepting it — Entries 2 and 4 both improved as a direct result.

---

## Phase 2 — Constraint change: zero infrastructure

### Entry 6 — Removing all external dependencies, and the bug it surfaced

**Task.** After the specification was finalized, I changed a constraint: the system must run with
no provisioned resources at all — no Docker, no database server. I asked whether the specification
needed to change.

**Output.** The assistant identified six affected areas and, importantly, confirmed that *nothing*
in the functional surface changed — all ~50 functional requirements, the API contract, the security
model and the state machine are infrastructure-agnostic, so the change is absorbed entirely below
the repository and scheduler ports.

Two findings were more valuable than the rest:

1. **It argued against the EF Core InMemory provider**, which was my instinctive choice. InMemory
   is not a relational database: it enforces no unique constraints, no foreign keys and no real
   transaction semantics. Tests for idempotency uniqueness (FR-1.12) and every concurrency case
   would have passed *without testing anything*. SQLite is a genuine ACID engine that still needs
   no installation.

2. **It caught a money-handling bug before a line was written.** SQLite has no `decimal` type, and
   EF Core maps `decimal` to TEXT — which sorts *lexicographically*, so `"9.99"` orders after
   `"100.00"`. FR-4.7 (sort by `totalAmount`) would have returned wrongly-ordered and wrongly-
   paginated results. EF Core reports this as a warning rather than an error, so it would very
   plausibly have shipped unnoticed. Mapping to REAL instead would have swapped a sorting bug for
   a precision bug in money.

**Issue.** The proposed fixes initially preserved the design at the cost of honesty in two places:
the original text claimed horizontal scalability, and the test strategy implied the scheduler's
locking behaviour was covered. Neither is true on SQLite — it has a single-writer lock and no
`FOR UPDATE SKIP LOCKED`.

**Correction.** Three changes:

- **Money is now stored as integer minor units (cents)** behind a `Money` value object. Integers
  sort, compare and aggregate exactly. This is also what payment processors do — the constraint
  forced a better decision earlier, rather than imposing a workaround.
- **§10.3 now distinguishes "designed for" from "demonstrated at"** horizontal scale, instead of
  claiming a property a single-file database cannot provide.
- **§12.4 was added to state the test-fidelity gap outright**: the PostgreSQL claim strategy cannot
  be exercised by this suite. `IPendingOrderClaimer` is therefore a port with provider-agnostic
  contract tests, so the same assertions run against PostgreSQL whenever one is available.

**Why this entry matters.** It is the clearest case of AI catching a defect I would not have: the
decimal-sorting problem is invisible until someone sorts a page of orders by total and the results
are silently wrong. It also shows the limit of the tool — left alone it optimized for preserving
the specification's claims, and it took a deliberate push to make the document admit what the
constraint genuinely costs.

---

## Phase 3 — Implementation

### Entry 7 — Scaffolding, and two corrections the AI needed

**Task.** Scaffold the solution: seven projects, dependency wiring, package management, build
configuration.

**Output.** Solution created with the layering from §4, central package management, warnings-as-
errors, and a runnable API exposing Swagger and health endpoints. Verified: clean build with zero
warnings, tests passing, all three endpoints returning `200`.

**Issue 1 — a specification assumption that did not survive contact with the machine.** The
specification named .NET 8, but only the .NET 10 SDK was installed. Targeting `net8.0` would have
*built* but not *run*, because rolling forward across a major version needs an explicit
`rollForward: LatestMajor`. That would have been a hidden trap for anyone cloning the repository,
and directly contradicted the "clone and `dotnet run`" promise in §3.2.

**Correction 1.** Retargeted to .NET 10, which is itself LTS and is now the current one. Nothing
in the design depended on the version — `TimeProvider`, `BackgroundService` and `PeriodicTimer`
all carry forward. The specification and decision log were updated rather than left stale.

**Issue 2 — a nested MSBuild file silently shadowing the root.** I added
`tests/Directory.Build.props` to relax a few analyzer rules for test projects. MSBuild stops at
the *first* `Directory.Build.props` it finds walking upward, so this silently discarded every
root-level setting for all three test projects — including `TreatWarningsAsErrors`. The build
would still have succeeded, which is what makes the failure mode dangerous: the safety net would
have been quietly removed with no signal.

**Correction 2.** Added an explicit `GetPathOfFileAbove` import so the root file is chained rather
than replaced, with a comment explaining why the import is load-bearing.

**Issue 3 — a licensing problem in a recommended package.** The specification named
FluentAssertions. Version 8 onward requires a paid licence for commercial use.

**Correction 3.** Switched to Shouldly (BSD). Equivalent expressiveness, no licensing exposure.

**Observation.** All three issues share a shape: the AI produced something that *builds and runs*
while being subtly wrong about the environment or its constraints. None would have been caught by
a compiler or a test. Two were caught by verifying against the actual machine rather than
reasoning about it, which is the practical lesson — the assistant is confident about environments
it cannot see.

### Entry 8 — The domain core, and a contradiction the code found in my own specification

**Task.** Implement the `Money` value object, the role-aware transition matrix, and the `Order`
aggregate, with tests.

**Output.** 213 passing tests. `Money` in integer minor units; the transition matrix as a frozen
`(actor, from) → allowed-to` lookup; `Order` enforcing every status change through a single
`TransitionTo` guard.

**Issue 1 — the specification contradicted itself, and only writing the code revealed it.**
FR-3.5 said a same-status transition is an idempotent no-op returning `200`. FR-5.7 said
cancelling an already-cancelled order returns `409`. Cancelling a cancelled order *is* a
same-status transition, so the two rules could not both hold. The §6.3 table was worse: it read
`❌ 409 idempotent no-op`, which is two different behaviours in one cell.

This had survived a full specification review because prose can hold a contradiction comfortably.
Code cannot — the method had to return something, and the ambiguity became a compile-time
decision.

**Correction 1.** Resolved uniformly in favour of the idempotent no-op: cancelling an
already-cancelled order returns `200` and never overwrites the original `CancelledBy`,
`CancellationReason` or `CancelledAt`. The reasoning is that the caller is requesting a state the
system is already in, so there is nothing to reject, and a retry-safe endpoint is worth more than
a technically-defensible error. FR-3.5, FR-5.7 and the §6.3 table were all updated.

**Issue 2 — a test that could not fail.** My first instinct for the transition-matrix test was to
iterate the production lookup table and assert each entry matched. That is tautological: it would
pass against any implementation, including one that permitted every transition.

**Correction 2.** The expected matrix is now declared longhand in the test file, independently of
the production table, and all 75 `(actor × from × to)` combinations are generated and checked
against it. To prove the suite actually bites, I mutated the production table to give customers
the admin cancellation window and re-ran: **4 tests failed**, then passed again once reverted. A
test suite that has never been seen to fail is an assumption, not evidence.

**Issue 3 — analyzer errors the AI did not anticipate.** Three build failures under
`TreatWarningsAsErrors`: a self-comparison (`small <= small`) flagged as CS1718, `.Last()` on an
indexable collection flagged as CA1826, and a `void`-returning `Cancel` used in an expression.

**Correction 3.** All three fixed — and the third was worth more than the fix: making `Cancel`
return `bool` to match `TransitionTo` is how the FR-3.5/FR-5.7 contradiction surfaced in the first
place. The strict build settings paid for themselves within an hour of being switched on.

**Why this entry matters.** The most useful thing the assistant did here was not writing the code;
it was that implementing the code forced a latent ambiguity in the specification into the open.
The lesson generalises: a specification that has only been read is less tested than one that has
been implemented.

### Entry 9 — Implementation: three bugs the AI wrote, and one it helped me find

**Task.** Implement persistence, the background job, the API layer, security, and the integration
test suite.

**Output.** A working system: 269 passing tests, a clean Release build with warnings as errors, and
a verified end-to-end run starting from a deleted database.

**Issue 1 — a latent configuration bug that only the auth tests could surface.** The JWT setup read
the signing key *eagerly* from `IConfiguration` at service-registration time, while
`JwtTokenIssuer` read it *lazily* through `IOptions`. In the running application both saw the same
value, so it worked. In the integration tests, where `WebApplicationFactory` layers configuration in
afterwards, the issuer signed with one key and the validator checked against another. Every
authenticated request returned a bare `401` with nothing to explain it.

**Correction 1.** Configured `JwtBearerOptions` through `IOptions<JwtOptions>` so both sides resolve
the same value at the same time. This was not merely a test problem: any late-layered configuration
source — a secret store, an environment-specific provider — would have produced the same silent
failure in production, with an unexplained `401` as the only symptom.

**Issue 2 — a `CreatedAtAction` that could never resolve.** Order creation returned HTTP 500.
ASP.NET Core strips the `Async` suffix from action names, so `CreatedAtAction(nameof(GetByIdAsync), …)`
was looking for an action that did not exist. The order *was* persisted; only the response failed —
the worst shape of bug, because a client would retry and duplicate the order.

**Correction 2.** Switched to `CreatedAtRoute` with an explicit route-name constant. Found by
running the API and calling it, not by reading the code: the compiler and the type system were both
perfectly happy with it.

**Issue 3 — convoluted replay detection.** To choose between `201` and `200` on an idempotent retry,
the first version inferred the answer from `CreatedAt != UpdatedAt` combined with a history-count
check. Fragile and unreadable.

**Correction 3.** Had the service return `CreateOrderResult(Order, WasCreated)` and let the
controller branch on a boolean. Inferring state the producer already knows is a smell worth naming.

**Issue 4 — a test that hung instead of failing.** After writing the concurrency tests I mutated the
claim SQL, replacing the atomic `UPDATE … RETURNING` with a plain `SELECT`, to check the tests would
catch it. They did not fail — they **hung**. The test's claim loop ran `while (true)` until a batch
came back empty, and a claimer that never transitions rows never returns empty.

**Correction 4.** Bounded the loop with an explicit iteration cap and a descriptive failure message.
The same mutation now produces 5 red tests in about a second instead of a stuck build. This was the
most valuable thing the mutation exercise produced, and it was a defect in my *tests* rather than my
code — a suite that hangs on regression is nearly as unhelpful as one that passes.

**What mutation testing established.** Two deliberate breaks, both caught:

| Mutation | Result |
| --- | --- |
| Customers granted the admin cancellation window | 4 domain tests failed |
| Claim statement's atomicity removed | 5 integration tests failed |

Both suites passed again on revert.

**Observation on the division of labour.** The assistant was fast and broadly correct on structure —
layering, EF configuration, DTO mapping, test scaffolding. Every genuine defect fell into one of two
categories: **framework behaviour it could not observe** (the `Async` suffix stripping, the
configuration ordering) or **runtime behaviour it could not predict** (the hanging test loop). None
were caught by the compiler, by the analyzers, or by reading the code. All were caught by running
the system and deliberately trying to break it.

The rule that came out of this project: treat AI output as a confident first draft from someone who
has never run the program.

### Entry 10 — A code review found the architecture claim was false

**Task.** A reviewer's first finding: *"Your use cases live in Infrastructure, not Application.
`OrderService` — the actual orchestration layer — sits in `OrderProcessing.Infrastructure.Orders`,
while `OrderProcessing.Application` contains only two interfaces and some DTOs. Why is your
business orchestration in the infrastructure project?"*

**Verification.** The criticism was correct and easy to confirm. The Application project contained
**four files**: two port interfaces, an exceptions file and a DTO file. No use cases. And
`OrdersController` imported `OrderProcessing.Infrastructure.Orders` directly.

**Root cause.** `OrderService` needed a `DbContext`, so it was placed where the `DbContext` lives.
That is the whole mistake in one sentence: **the dependency dragged the use case into
infrastructure instead of being inverted.** The layer names then described an intention rather
than the code, which is worse than having no layer names — the README asserted a shape the
solution did not have.

The assistant had also produced a trade-off note in the README justifying the absence of a
repository abstraction ("`DbContext` already *is* Repository + Unit of Work; wrapping it is
ceremony"). That argument is only valid if the use cases live in infrastructure. Once they must
move, the abstraction stops being ceremony and becomes the mechanism that lets them move. A
plausible-sounding justification for the wrong structure is a failure mode worth noting: it reads
as considered design, and it survived my own review.

**Correction.** Moved orchestration to where it belongs:

| Moved | From | To |
| --- | --- | --- |
| `OrderService` | Infrastructure | **Application** |
| `OrderPromotionService` | Infrastructure | **Application** |
| `ClaimsPrincipal → Actor` | Infrastructure | **Api** (it is an ASP.NET type) |
| `AuthConstants` | Infrastructure | **Application** (shared by Api and DI) |

Added `IOrderRepository`, `IProductCatalog` and `OrderScope` as application-owned ports, with
`EfOrderRepository` / `EfProductCatalog` as infrastructure adapters. No `IQueryable` or
`DbContext` now crosses into Application, and `DbUpdateConcurrencyException` is translated into a
domain-level `ConcurrencyConflictException` at the repository boundary so the API no longer needs
to know which ORM is in use.

**A second, quieter defect the same review exposed.** The README claimed query scoping was
"fail-closed: omitting it returns *nothing*, not *everything*." That was simply untrue of the old
code — `ScopeToCaller` was a helper you had to remember to call, and forgetting it returned **every
customer's orders**. An overstated security claim is worse than an absent one.

The refactor made the claim true rather than merely softening it: `OrderScope` is now a **required
parameter** on every repository read. There is no overload that omits it, so the compiler enforces
what a naming convention previously only suggested.

**A bug I introduced during the refactor.** Moving the change-tracker reset into
`AddStatusHistory` put `ChangeTracker.Clear()` *before* `SaveChangesAsync`, which discarded the
very audit rows being added. One integration test caught it immediately —
`A_promotion_is_recorded_in_the_audit_trail_as_a_system_action`. Clearing now happens before the
add, which satisfies both intents. Worth recording because it is the case *for* having tests that
assert on the audit trail rather than only on final status.

**Preventing recurrence.** Two things, not one.

*First*, `ArchitectureTests` — five assertions that fail the build if the domain or application
layer acquires a persistence or web dependency, or if the use cases drift back out of Application.
Mutation-verified: introducing an EF Core type into `OrderService` fails the suite.

The first mutation attempt was instructive. Merely adding an unused `PackageReference` did **not**
fail the test, because `GetReferencedAssemblies` reports only assemblies the compiler actually
bound to. That is arguably the right sensitivity — an unused reference is inert, while a single
`DbContext` parameter in a use case breaks substitutability — but the limitation is now documented
on the test rather than left as an unexamined assumption.

*Second*, and more telling: the previously-empty `OrderProcessing.Application.Tests` project now
holds **21 unit tests** covering the order use cases against substituted ports — no database, no
migrations, no host. Those tests were impossible to write before the refactor, because the use
cases needed a `DbContext`. Their existence is the concrete payoff of the dependency rule, and
their absence was a signal I had not read: *an empty application-test project is evidence that
there is no application layer to test.*

Several of them assert on security behaviour that integration tests can only observe indirectly —
for example that a customer's list request reaches the repository with the caller's own scope and
a discarded `customerId`, rather than merely that the response contained no foreign rows.

**A note on the specification.** Checking §4 afterwards produced an uncomfortable finding: the
specification had described the correct structure all along, naming `IOrderRepository` and placing
use cases in Application. The implementation simply diverged from it, and nothing — not the
document, not my review, not the test suite — noticed. A specification is only as good as the
mechanism that checks the code still matches it, which is now `ArchitectureTests`.

**Why this entry matters most.** Every earlier entry describes a bug in code. This one is a defect
in **architecture**, and it survived the specification phase, the implementation, my own review,
and a written architectural explanation. It was found by an outside reader asking one direct
question about where a class lived. The AI produced the structure I asked for and then described it
as clean architecture; neither of us checked whether the description matched the directory it had
been written into.

The generalisable lesson: **layer names are claims, and claims need tests.** A README asserting a
dependency rule proves nothing. `ArchitectureTests` does.

### Entry 11 — A review point about documentation uncovered two real bugs

**Task.** A second review finding: *"The background job does not route through `Order.TransitionTo`.
Both the README and the XML doc say the matrix is enforced in exactly one place that the
controllers and the job both call. But `SqlitePendingOrderClaimer` sets `Status = 'Processing'` in
raw SQL… The behaviour is still correct, but the enforcement is duplicated, in SQL, not
centralised. That's the actual trade-off: you gave up single-point enforcement to get atomic
claiming."*

**The framing was exactly right**, and more generous than the code deserved. The reviewer
characterised it as a documentation defect with a defensible trade-off underneath. Checking the
trade-off is where it stopped being defensible.

**Bug 1 — the promotion was not transactional.** `SqlitePendingOrderClaimer` could enlist in an
ambient transaction, but *nothing ever opened one*. So the claim statement auto-committed the
status change, and the audit rows were written by a separate `SaveChanges` afterwards. A crash
between the two leaves an order `PROCESSING` with **no history row** — a direct violation of
FR-6.5, in the one code path whose entire purpose is changing status.

The duplicated rule the reviewer spotted was the visible symptom; the missing transaction was the
defect underneath it. Both had the same cause: collapsing "claim this order" and "transition this
order" into a single SQL statement conflated a concurrency concern with a business rule.

**Correction 1.** Separated them. A claim is now a **lock, not a transition** — it stamps a lease
and changes nothing else. Promotion loads the claimed aggregates and calls
`Order.PromoteToProcessing`, so the matrix is genuinely the single point of enforcement, and
status plus audit entry commit together in one transaction. The lease is an EF Core *shadow
property*, so the scheduling mechanism does not appear on the domain aggregate, and it is cleared
in the same transaction — there are no stale leases to reap.

A pleasing side effect: `OrderStatusHistory.ForSystemPromotion` could be deleted. It existed only
to hand-build the audit row the bypass had skipped, and its own XML comment argued it was "not a
general-purpose bypass" — a comment defending a design that should not have existed.

**Bug 2 — the integration tests were not isolated.** Writing a test to prove the audit gap, I found
**204 orphaned orders** out of **863** — in a suite that creates perhaps eighty. The database was
not the per-factory in-memory one the fixture configures; it was a **file on disk**, accumulating
across every run of the day, holding relics of every earlier version of the promotion code.

The cause was identical to the JWT defect in Entry 9: `AddPersistence` read
`configuration.GetConnectionString("Default")` **eagerly at registration**, so the test harness's
override — layered in afterwards — was ignored and the default `orders.db` used instead. The same
mistake, in the same file, surviving the same review that fixed its twin.

**Correction 2.** The `DbContext` now resolves its connection string lazily via the
`IServiceProvider` overload, and `ApiFactory` asserts at startup that it actually got an in-memory
database, failing loudly otherwise. Verified by running the suite twice and confirming no
`orders.db` is produced.

**What this says about the earlier test results.** Every integration run before this point was
executing against shared, accumulating state. They passed, and they were testing real behaviour —
but they were not independent, and a test that depends on execution history is not a regression
guard. This does not invalidate the findings in earlier entries, but it does mean "the suite
passed" carried less weight then than it does now.

**Why this entry matters.** The reviewer reported a wording problem. Taking it seriously — rather
than editing the sentence to match the code, which was the tempting fix — surfaced a durability bug
in the audit trail and a correctness-of-testing bug that invalidated the suite's independence.

Two lessons worth keeping:

- **A documentation defect is a hypothesis that the code is wrong.** The gap between what a system
  claims and what it does is rarely only in the prose.
- **Fixing a bug is not the same as fixing its class.** Entry 9 corrected one eagerly-read
  configuration value. The identical mistake sat six lines away in the same method and was not
  looked for. Asking *"where else does this pattern appear?"* is worth more than the individual
  fix.

**Where AI helped most**

- Breadth of concerns raised unprompted — idempotency keys, `404`-versus-`403`, price snapshots,
  timer self-overlap, the OWASP BOLA framing
- Catching the SQLite decimal-sorting defect at design time, before a line was written
- Speed on mechanical, well-patterned work: EF configurations, DTO mapping, test scaffolding

**Where it needed correcting**

- **Domain realism.** It optimises for fidelity to the written brief, so the admin cancellation
  requirement — absent from the brief — was absent from the output until I raised it.
- **Environmental assumptions.** Confident about the installed SDK, package licensing and framework
  naming conventions that it had no way to verify.
- **Specification contradictions.** It reproduced my inconsistent requirements faithfully instead of
  flagging them; only implementing them forced the conflict into the open.
- **Test rigour.** Its instinct was to derive expected values from the implementation, which
  produces tests that cannot fail.

**What I would tell another candidate.** The value is not in generating code faster; it is in having
a tireless collaborator that raises concerns you would not have thought to consider. But it has
never run the program, never seen the machine, and will not tell you when your own requirements
contradict each other. Verification is the part that stays yours.

### Entry 12 — The third review point, and a regression the fix exposed

**Task.** A third finding: *"The idempotency check is read-then-write, not atomic. Two concurrent
requests with the same key both miss the read and both insert. The unique index catches it — but
as a `DbUpdateException`, which your handler almost certainly maps to a 500, not to 'return the
original order.' So the happy path works and the genuine race returns an error."*

**Verified before touching anything.** A test firing eight concurrent creates confirmed the
diagnosis, and found the problem is wider than idempotency: **`OrderNumber` races too.** It is
allocated as "highest for the year, plus one", so concurrent callers read the same maximum and
collide on `UX_Orders_OrderNumber`. Eight concurrent creates *with no idempotency key at all*
returned **HTTP 500**. Any concurrent order creation failed, not just idempotent retries.

The reviewer identified the pattern; the pattern had two instances.

**Correction.** The insight is that a read cannot enforce uniqueness — the database already does,
and the violation is the signal that someone else won. Both constraints are now translated at the
repository boundary and handled according to what the collision *means*:

| Collision | Meaning | Response |
| --- | --- | --- |
| Idempotency key | Another request created this order | Re-read the key, return the winner's order, `200` |
| Order number | Two requests allocated the same value | Transient; retry with a fresh number, bounded at five attempts |

Twelve concurrent requests sharing a key now produce one `201`, eleven `200`s and exactly one
order. Verified both in the suite and against a running server with real parallel HTTP.

**A regression the fix uncovered.** Adding the new arms to the exception-to-status switch produced
a compiler error: *"The pattern is unreachable."* Investigating it revealed that
`ConcurrencyConflictException` — which I introduced during Entry 10's refactor — derives from
`DomainException`, and the general `DomainException => 400` arm sat **above** it. Every
optimistic-concurrency conflict had been answering **400 Bad Request instead of 409 Conflict**
since that refactor, silently, with tests passing.

C# switch arms are order-sensitive and the compiler only warns when shadowing is *total*. A
general arm above a specific one that is merely *reachable by other means* produces no diagnostic
at all. The mapping is now extracted into `ProblemDetailsMapper` and pinned by a theory covering
every arm, including one test named for this specific regression.

**Observation on review as a technique.** Three review points have now produced, between them: a
misplaced application layer, an untestable-by-construction security claim, a non-transactional
audit trail, a test suite that was never isolated, two unhandled write races, and a status-code
regression. Only two of those were the thing the reviewer actually pointed at.

The pattern is consistent enough to be worth naming: **a review finding is a symptom, and the
useful question is not "is this true?" but "what else is true if this is?"** Each point was
correct on its own terms and understated on investigation. Treating a finding as a hypothesis to
test — rather than a defect to patch — is what turned three comments into six fixes.

### Entry 13 — "What if two products have different currencies?"

**Task.** A fourth finding: *"Currency is taken from an arbitrary product.
`var currency = products[productIds[0]].Currency;` picks the first requested product's currency; if
a later line differs, `Order.Create` throws a bare `InvalidOperationException` — again a 500 rather
than a 400. Multi-currency is out of scope, which is fine, but 'what if two products have different
currencies?'"*

**Why it had gone unnoticed.** Every seeded product is priced in USD, so no test — and no manual
exercise of the API — could construct a mixed-currency request. The path was unreachable *from the
data*, not from the code. A catalogue containing a single euro-priced product would have exposed it
immediately. Seed data that only covers the happy case quietly decides which branches get tested.

**Two problems, one root cause.** The visible defect is the status code. The underlying one is that
`Order.Create` *accepted* the currency as a parameter, which made "all lines share a currency" a
precondition the aggregate trusted rather than an invariant it enforced. A caller then had to pick
one, and picking the first product's made correctness depend on request ordering — the same request
with its items listed in the other order would have compared against a different baseline.

**Correction.** The currency is now **derived** from the lines: `Order.Create` reads their distinct
currencies and rejects the order unless there is exactly one. The parameter is gone, so no caller
can pick arbitrarily. The rejection is a `MixedCurrencyOrderException : DomainException` mapping to
`422`, naming every currency involved, with the list sorted so the message does not depend on
request ordering.

An unexpected simplification fell out: the old code checked each line against the order currency
*inside the item loop*, part-way through building the aggregate. Deriving up front removed that
check entirely, and with it a path that could throw after items had already been added.

**Auditing the class rather than the instance.** Following the discipline the previous entries
established, I checked every framework exception the domain and application layers can throw for
reachability from a well-formed request:

| Exception site | Reachable from a request? | Verdict |
| --- | --- | --- |
| Currency mismatch in `Order.Create` | **Yes** | Fixed — now a `DomainException` → 422 |
| Quantity below one in `OrderItem.Create` | No — `[Range(1,1000)]` rejects it first | Correct as an argument exception |
| Sub-cent precision in `Money.FromDecimal` | No — prices originate in the catalogue | Correct |
| Empty-GUID guards | No — the `{id:guid}` route constraint rejects first | Correct |
| Cross-currency arithmetic in `Money` | Only via the mismatch above | Resolved by the same fix |

The distinction worth keeping: a **business rule a client can violate** belongs in a
`DomainException` with a real status code; a **programming error** should stay a framework
exception and produce a 500, because that is what it is. Blanket-mapping `ArgumentException` to
400 would have been the quick fix and would have masked genuine bugs.

**Verification.** Mutation-tested by restoring "first line wins": **six tests failed** across the
domain and integration suites. Four new integration tests add a euro-priced product so the
rejection is observed over HTTP — including one asserting that a consistent non-USD order still
succeeds, so the fix cannot have over-corrected into rejecting all non-default currencies.

---

## Summary
