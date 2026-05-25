# Test Suite Reshaping — collapse the EF-InMemory middle tier

**Status:** Draft for review (no tests changed yet)
**Date:** 2026-05-25
**Author:** Peter + Claude (design dialogue)

## Problem

The suite is ~3,700 cases and growing. The pain that surfaced this: a trivial
service test (`SignUp_RequireApprovalRota_UserMissingConsents_StaysPending`)
intermittently **fails by hitting the 5-second `HumansFact` timeout** — not
because it does 5 seconds of work, but because it stands up a full EF model +
in-memory DB and, when it lands first or the box is loaded, the cold-start
(model build + JIT + `SaveChanges`) blows the per-test ceiling.

Peter's hunch: we have too many tests hitting the in-memory DB; testing
**business logic** and **integration** *separately* would be sufficient. The
data says the hunch is right, and the target is more specific than "3,700."

## Current state (measured 2026-05-25)

| Tier | Tests | Files | DB | Verdict |
|---|---:|---:|---|---|
| Pure-logic (mocked repos) | **1,534** | 199 | none | ✅ keep — cheap, fast, correct shape |
| `ServiceTestHarness` service tests | **877** | 45 | EF InMemory | ⚠️ convert to pure-logic |
| Repository / caching / misc | **~365** | 43 | EF InMemory | ⚠️ move query tests to real DB; mock the rest |
| `Humans.Integration.Tests` | 100 | — | **Testcontainers Postgres** | ✅ real tier already exists — grow it |
| Analyzers / Domain / Web | ~400 | — | none | ✅ keep |

The majority of `Humans.Application.Tests` (1,534 / 2,790) is *already* pure unit
tests paying no DB cost. The cost and the flakiness come from the **1,242
EF-InMemory tests**, with the **877 `ServiceTestHarness` tests** as the core
problem.

## Why EF InMemory is the wrong tool

EF InMemory is the "ice-cream-cone" anti-pattern — the worst of both worlds:

- **Not integration confidence.** No SQL, no constraints, no transactions, no
  real query translation. It happily passes LINQ that throws on Postgres (and
  vice versa). Microsoft itself steers users off it for anything DB-shaped.
- **Not cheap like a unit test.** Full EF model build + change tracking +
  per-test database stand-up. This is what trips the 5s timeout.

So the 877 service tests pay **integration-tier cost for unit-tier confidence**,
and the EF-InMemory repository/query tests give **false confidence** because the
provider doesn't behave like production Postgres.

`ShiftSignupServiceAutoConfirmIgnoresConsentTests` is the poster child: a real
repository + a real `ShiftManagementService` + a whole in-memory DB, stood up to
assert one branch — `policy == Public || canApprove`. As a pure unit test with a
mocked `IShiftSignupRepository`/`IShiftManagementService`, it's microseconds and
states its intent more clearly.

## Target architecture — the proper pyramid

1. **Business logic → pure unit tests** (mocked repository + cross-section
   service interfaces). Fast, deterministic, the bulk. This is the home for the
   877 service tests.
2. **Persistence / queries → real-DB integration tests** against
   **Testcontainers Postgres**. This is where query shapes, includes, filters,
   constraints, and triggers are verified — against the provider we actually
   ship.
3. **A few end-to-end flows** through `HumansWebApplicationFactory` (already
   present) for wiring/authz/route coverage.

Then **delete EF InMemory entirely** and guard against its return.

## Decision: Testcontainers Postgres, not SQLite

We discussed SQLite-in-memory vs Testcontainers Postgres for the real-DB tier.
**Recommendation: Testcontainers Postgres** — and this is largely already
decided by precedent:

- `HumansWebApplicationFactory` **already boots a real Postgres container**
  (`Testcontainers.PostgreSql`, container per `IClassFixture`).
- There is **already a documented real-DB repository-test pattern**
  (`VolunteerTrackingRepositoryTests`): resolve a scoped `HumansDbContext` from
  the factory, exercise the repository against real Postgres.
- The schema uses Postgres-specific behavior (append-only `ConsentRecord` via DB
  triggers, NodaTime mappings) that only real Postgres validates.

Introducing SQLite would add a **third** database technology and split the
real-DB pattern — a reuse-first violation. We extend the Testcontainers tier
that exists.

> **Peter: this is the one load-bearing call I made for you. If you'd rather pay
> for SQLite's no-Docker speed at the cost of a third DB tech, say so and the
> migration structure below is unchanged — only the harness implementation
> differs.**

## Migration strategy

Per EF-InMemory file, **classify** then act:

- **Logic-with-incidental-DB → convert to pure unit test.** The service has real
  branching/orchestration; the DB is just a data source. Mock the repository
  interface (and any cross-section `I…ServiceRead`). This is most of the 877.
- **Query/repository test → move to Postgres integration tier.** It's really
  testing how a query materializes. Move it next to
  `VolunteerTrackingRepositoryTests` and run it against real Postgres. Fewer, but
  honest.
- **Genuinely needs EF tracking/save-ordering across entities → move to
  integration.** Rare; don't fake it with mocks.

Rules of the road:

- **Section by section, one PR per section.** No big-bang. Camps/Shifts/Teams etc.
- **Net coverage moves tiers, it is not dropped.** Behavior a `ServiceTestHarness`
  test used to catch via real navigation loading must be covered by the
  Postgres repository/integration tests for that section before its service
  tests drop to mocks.
- **Don't mechanically cull.** This is a strategy change, not slop removal —
  `/trim-tests` (Stryker) is the wrong tool here. (Stryker is still useful
  *after* a section converts, to confirm the mocked tests hold the mutation
  line.)

## Pilot: Shifts

Shifts is where this started and has a clean spread across both buckets.

**Convert to pure-logic (mock `IShiftSignupRepository` / `IShiftManagementService`):**

| File | Tests |
|---|---:|
| `Services/Shifts/ShiftSignupServiceTests.cs` | 32 |
| `Services/Shifts/ShiftManagementServiceTests.cs` | 30 |
| `Services/Shifts/ShiftDashboardMetricsTests.cs` | 30 |
| `Services/Shifts/ShiftManagementServiceCoveragePiesTests.cs` | 13 |
| `Services/Shifts/Workload/WorkloadServiceTests.cs` | 14 |
| `Services/Shifts/GeneralAvailabilityServiceTests.cs` | 6 |
| `Services/Shifts/ShiftSignupServiceFilterIncompleteOnboardingTests.cs` | 5 |
| `Services/Shifts/ShiftSignupServiceAutoConfirmIgnoresConsentTests.cs` | 4 |
| `Services/Shifts/ShiftSignupServiceCoverageGapTests.cs` | 2 |
| `Services/Shifts/ShiftSignupServiceEarlyEntryTests.cs` | 2 |

**Move to Postgres integration tier (query/repository behavior):**

| File | Tests |
|---|---:|
| `Services/Shifts/ShiftSignupRepositoryActiveCommittedTests.cs` | 3 |
| `Repositories/Shifts/*` (the EF-InMemory repo tests) | — |

**Pilot exit criteria:** Shifts has zero `UseInMemoryDatabase`; the section's
query behavior is covered against real Postgres; record the **count delta** and
**wall-clock delta** (cold + warm) for the section. Decide roll-out from real
numbers, not estimates.

## Risks & mitigations

- **Coverage loss when service tests become mocks.** Mitigation: move the
  navigation-loading / save-ordering coverage to the Postgres repo tests *first*;
  spot-check the converted section with Stryker.
- **Misclassification** (calling an integration test a unit test). Mitigation:
  per-file human review in the section PR; when in doubt, send it to Postgres.
- **Container isolation.** The factory shares one container per class; the
  existing pattern relies on random-GUID isolation, not per-test reset. Moving
  ~27 repo tests over needs a consistent isolation story — either keep
  GUID-discipline or add truncate/Respawn between tests. Decide in the pilot.
- **Docker dependency** on dev + CI. Already required by the existing integration
  tier; accepted.
- **Effort.** 877 + ~365 tests is large. The section-by-section split keeps each
  PR reviewable and lets us stop early if the delta isn't worth it.

## Guardrail — prevent EF InMemory creep back

Once a section is migrated, lock it: add a Roslyn analyzer (next free `HUM00xx`)
that flags `UseInMemoryDatabase` / `Microsoft.EntityFrameworkCore.InMemory`
references in test projects. Per Peter's preference, an analyzer (not a test)
enforces this call-site rule, with a `[Grandfathered]` baseline for
not-yet-migrated sections that ratchets to zero. This is the *final* phase —
meaningless until migration is underway.

## Sequence

1. **This doc** — agree direction + the Postgres-vs-SQLite call.
2. **Pilot PR: Shifts** — convert + move + measure. One PR.
3. **Review the delta** — count + wall-clock. Go / no-go on roll-out.
4. **Roll out section-by-section** — one PR each, biggest EF-InMemory sections
   first by value (TeamService 127, Expenses 67, Camp 57, ApplicationDecision 43,
   Issues 42…).
5. **Guardrail analyzer** — ratchet `UseInMemoryDatabase` to zero.

## Open decisions for Peter

1. **Postgres vs SQLite** for the real-DB tier (recommendation: Postgres — see
   above).
2. **Pilot section** — Shifts proposed; any reason to start elsewhere?
3. **Container isolation** approach for the moved repo tests — GUID-discipline vs
   truncate/Respawn (can defer to the pilot).
