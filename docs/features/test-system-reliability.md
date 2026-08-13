<!-- freshness:triggers
  .github/workflows/build.yml
  tests/xunit.runner.json
  tests/Humans.Integration.Tests/Infrastructure/HumansWebApplicationFactory.cs
  tests/Humans.Integration.Tests/Infrastructure/HumansTestDatabase.cs
  tests/Humans.Integration.Tests/Infrastructure/IntegrationTestBase.cs
  src/Humans.Web/Program.cs
-->
<!-- freshness:flag-on-change
  CI test filtering, Testcontainers fixture strategy, Hangfire Testing-environment guards, and the failing-test policy. Review when the build workflow, xunit config, or integration test fixtures change — phase statuses here go stale as the plan lands.
-->

# Test System Reliability

Rebuild the test setup so the suite is a reliable signal again — CI catches what local sees, integration tests survive concurrent runs, and "pre-existing failures on main, doesn't block the merge" stops being a sentence anyone says.

## Why this exists

PRs keep landing with the sentence *"the agent reported N pre-existing failures on `origin/main` unrelated to this PR — doesn't block the merge."* That happens because:

1. **CI does not run integration tests.** `.github/workflows/build.yml:82` filters them out (`--filter "FullyQualifiedName!~Integration"`). Integration failures only surface when someone runs locally, then get attributed to "pre-existing" and merged around.
2. **EF In-Memory** is used in 64 test files repo-wide (15 of them in `Humans.Application.Tests`; the rest sit in the per-section test projects), counting both direct `UseInMemoryDatabase` calls and use of the shared `TestDbContextFactory`. It doesn't enforce FKs, NOT NULL, unique constraints, doesn't translate Npgsql LINQ, doesn't fire triggers — so unit tests pass while real-Postgres behavior diverges.
3. **Per-class Testcontainers Postgres.** ~18 integration test classes × `IClassFixture<HumansWebApplicationFactory>` × no parallelization control = up to 18 concurrent Postgres containers booting, each running all 96 migrations. Resource contention causes intermittent failures.
4. **Hangfire static state leakage.** `JobStorage.Current` is per-AppDomain. The codebase has four `if (!IsEnvironment("Testing"))` guards, all in `Program.cs`. Every new Hangfire-touching feature is one missed guard from breaking tests — this is the "Hangfire-init" failure cluster pattern.
5. **Failures are tolerated.** A test that starts failing can sit on `main` indefinitely because "pre-existing, not my PR" is accepted.
6. Noise: `longRunningTestSeconds: 1` in `xunit.runner.json` floods integration runs with diagnostics, training the team to ignore xUnit output.

This is a horizontal — it doesn't belong to one section. Parent issue is `section:infra`. Child issues that fix a specific section's tests get that section's label.

## Phases

Each phase is one or more independent PRs off `main` (this is not `one-branch-for-phased-plans` — these are independently shippable; some span months). Order matters for P0–P3; P4 batches in parallel after P3.

### P0 — Fix the 53 existing integration test failures
**Value: high · Effort: medium · Risk: low. Tracking: nobodies-collective/Humans#762.**

Before turning anything on in CI, the existing failure backlog gets fixed. Bucket by cluster (Hangfire init, container race, schema drift, fixture state, etc.) — one PR per cluster, all linked to the P0 issue.

Skipping a failure with a tracking issue attached is not an alternative to fixing it; that just moves the backlog somewhere less visible. The act of triage will surface the actual root causes (Hangfire is the prime suspect; container race is second). P1 stays blocked on P0 — turning CI green is non-negotiable.

**Definition of done:** `dotnet test tests/Humans.Integration.Tests` returns 0 failures on `origin/main` HEAD. P0 issue closes.

### P1 — Turn integration tests on in CI
**Value: high · Effort: small · Risk: low. Depends on P0.**

Remove `--filter "FullyQualifiedName!~Integration"` from `.github/workflows/build.yml:50`. Either run integration tests in the same job (simplest) or as a separate job with Postgres service container (cleaner separation, allows per-job timeout tuning). Keep the existing `--blame-hang-timeout 2m` guard.

**Definition of done:** integration tests run on every PR. A new "pre-existing failure" cannot land on `main` without being noticed.

### P2 — Share one Postgres container across the assembly — **shipped**
**Value: high · Effort: medium · Risk: medium. Depends on P0. Landed via nobodies-collective/Humans#764.**

`HumansWebApplicationFactory` is registered once for the assembly with xUnit v3's `[assembly: AssemblyFixture(...)]` (the attribute form — v3 has no `IAssemblyFixture<T>` interface, and an assembly fixture cannot take another assembly fixture as a constructor argument). One container, one app boot, one migration pass per test run. Test parallelization is disabled for the assembly, since the classes now share one host. The migration-mechanics tests (`PhysicalDefaultParityTests`, `SectionMigrationRunnerTests`) and the localization sweep's second app boot take their own databases inside that one container rather than starting their own.

Measured back to back against `origin/main` at 94535e688, same machine, same command:

| | before | after |
|---|---|---|
| distinct Postgres containers created per run | 30 | **1** |
| test duration | 4m 27s | **25s** |
| result on an otherwise-idle machine | 122 passed, 1 skipped | 122 passed, 1 skipped |
| result while four other agents were building | **22 failed** (every one a 30s/60s timeout) | 0 failed |

That last row is the point of the phase: the "pre-existing failures" were never assertion failures, they were containers starving each other.

**Per-test database isolation did not ship in P2** — it landed separately as P2a below, after both options in the original plan turned out not to work as written.

**Definition of done:** integration suite boots one Postgres container per run ✅; suite runtime drops to seconds, not minutes ✅; per-test isolation → P2a.

### P2a — Per-test isolation — **shipped**
**Value: high · Effort: medium · Risk: medium. Depends on P2. Landed via nobodies-collective/Humans#983.**

#### The mistake both original options make

They treat the database as the unit of isolation. It isn't. The app carries the database's contents forward in process memory — 11 Singleton `Caching*Service` decorators, `TrackingMemoryCache`, `DevPersonaSeeder`'s resolved persona ids, Identity's security stamp cache, the DataProtection key ring. Those caches are a *projection of the database*, so resetting one without the other leaves the app asserting rows that no longer exist. Every mechanism that resets the database while keeping the app alive re-derives the same failure with a different reset verb.

That is not a hypothesis. Option (b) was built and measured, and 23 of 123 tests went down exactly this way.

#### Mechanisms considered

| Mechanism | Does it isolate? | Cost | Demands on test authors |
|---|---|---|---|
| GUID-suffixed keys (the status quo convention) | No. Nothing enforces it; a test that forgets is silently wrong. | free | remember it, every test, forever |
| `BEGIN … ROLLBACK` around the test | **No — verified, not assumed.** See below. | — | — |
| `TRUNCATE … CASCADE` between tests | Database yes, app no. 23/123 tests down. | cheap | — |
| Truncate + an app-wide cache flush | Only if the flush is *complete*, and nothing can establish that it is. Needs `Clear()` on the public `ICacheStats`, a fix to `CachingEventService`'s partial registration, plus an open-ended audit of every other singleton holding DB-derived state. Adds production surface whose only consumer is tests, guarded by an invariant nothing enforces — the next caching decorator silently un-isolates the suite, and the symptom is a false pass. | cheap | — |
| Per-test schema + `search_path` | Same app-state defect, plus Postgres has no `CREATE SCHEMA LIKE`, so the whole DDL would have to be replayed per test. | — | — |
| Per-test database, shared app | Incoherent — option (b) with a different verb. | — | — |
| **Per-test app + per-test database cloned from a migrated template** | **Yes, by construction.** The reset unit is all process-level state: fresh database *and* fresh caches, because it is a fresh app. | 0.3 s/test (below) | none |

#### Why (a) is not implementable — measured, not asserted

Two throwaway probes settled it against the real fixture. Creating a table inside a transaction on the test's own connection, then querying for it from an app scope resolved the way a TestServer request resolves one: the app scope did not see it. `SELECT pg_backend_pid()` shows why — the test's connection and every app scope's are different backends, because each scope rents its own from the pooled `NpgsqlDataSource`. The isolation a test's transaction buys is isolation *from the code under test*. Pinning the pool to one connection doesn't rescue it either: EF's own `SaveChanges` transaction then collides with the out-of-band `BEGIN`.

#### What shipped

Container lifetime and app lifetime are now separate objects, because they now have different lifetimes:

- **`HumansTestDatabase`** is the assembly fixture. It starts the run's one Postgres container, creates `humans_template` and migrates it by booting one throwaway app, then releases it. It hands out clones.
- **`HumansWebApplicationFactory`** takes a connection string and boots an app against it. It no longer owns a container.
- **`IntegrationTestBase`** clones a database and boots a factory in `InitializeAsync`, and disposes the factory and drops the database in `DisposeAsync` — per test.

`ResetSharedSubstitutes` is gone: the substitutes are fields on the factory, and the factory is now per-test, so there is nothing left to share. P7 is subsumed.

Measured on the same machine, per operation: `CREATE DATABASE … TEMPLATE` 33–59 ms, app boot on the pre-migrated clone 240–311 ms. Migrating from scratch instead of cloning would be 2,367 ms, which is what makes the template worth having.

`DatabaseIsolationTests` is the regression guard. It is a two-case `[Theory]` — xUnit gives each case its own class instance and therefore its own database — where each case writes a row under a *fixed* key and asserts exactly one such row exists. On a shared database the second case sees two and fails; the same test also asserts `/dev/login/volunteer` still redirects rather than 500s, which is the exact symptom that took option (b) down. It fails on the shared-database scheme and passes on this one.

#### What it costs, and what pays for it

Sequentially the suite went from **33 s to 2 m 39 s**. Only 279 ms of the ~1 s added per test is the fixture's own work (clone 39 ms, boot 227 ms, drop 13 ms); the rest is a cold app — EF model and query-plan caches, routing and policy caches, all rebuilt per host. That is inherent to making the app the reset unit, and cannot be optimised away without giving up the isolation.

What pays for it is that test classes no longer observe each other, which is the only reason nobodies-collective/Humans#764 disabled parallelization. Turning it back on brings the suite to **41 s – 1 m 10 s** across three consecutive green runs on a 32-core box also running other work. The container is started with `max_connections=500` so the parallelism is bounded by cores rather than by Postgres.

| | shared everything (P2) | per-test, sequential | per-test, parallel |
|---|---|---|---|
| suite wall clock | 33 s | 2 m 39 s | 41 s – 1 m 10 s |
| a test can see another test's writes | yes | no | no |

**Definition of done:** no test can see another test's writes ✅; the app's caches stay coherent with its database ✅; no production surface added ✅; the P2 runtime is not regressed ✅.

### P3 — Containerize Hangfire away from static state
**Value: high · Effort: medium · Risk: low. Can run in parallel with P2.**

`JobStorage.Current` and `RecurringJob.AddOrUpdate` are per-AppDomain statics. Wrap them behind interfaces (`IRecurringJobScheduler`, and the existing `IBackgroundJobClient`). Production binds to Hangfire; Testing binds to a no-op or an in-memory recorder substitute. Delete every `if (!IsEnvironment("Testing"))` guard in `Program.cs` and infrastructure.

For features that assert "the job was enqueued," verify via the abstraction substitute, not by inspecting Hangfire storage.

**Definition of done:** zero `IsEnvironment("Testing")` guards related to Hangfire in `src/`. Every feature that touches background jobs is testable without environment branching.

### P4 — Migrate Application repository tests off EF In-Memory
**Value: high · Effort: large · Risk: low. Depends on P2 (so the shared-fixture infra exists).**

64 files split into two camps:

- **Repository tests** (~30–40): they test LINQ translation. Must run against Postgres. Move onto the same shared-container fixture used by integration tests, or a slimmer per-assembly Postgres fixture inside `Humans.Application.Tests`.
- **Service tests using EF In-Memory as a stand-in for "any persistence"**: should not be touching a `DbContext` at all. Convert in place to mock the repository interface. (There is no longer a shared root context — `HumansDbContext` was deleted outright; each section owns its own.)

Ship **in batches by section** — one PR per section (Camps, Shifts, Events, Notifications, Profiles, Teams, Audit Log, Legal, Store, Tickets, Agent, …). Each batch is a `section:<name>` child issue with the appropriate section label. Side benefit: surfaces repositories that shouldn't have been reached through a DbContext in unit tests.

**Definition of done:** zero `.UseInMemoryDatabase(` references in `tests/`. Every section's tests run against real Postgres or against a mocked repository interface.

### P5 — Failures get fixed
**Value: medium · Effort: trivial · Risk: none. Can run alongside P0.**

- Add `memory/process/no-pre-existing-failures.md` — a test failing in CI gets fixed. *"Pre-existing, not my PR"* stops being a valid sentence.

That is the whole phase. It is a written rule, not a mechanism.

There is no lint rule and no `/maintenance` sweep. Both were specced here originally and both were wrong: a check that polices skip strings for issue references, plus a monthly sweep to notice when one of those issues has since closed, is machinery for keeping broken tests around in a managed state. The rule is that failures get fixed, so there is nothing to manage. (Built and then removed in peterdrier/Humans#1180 — the CI grep reached 279 lines of attribute parsing, chasing legal C# spellings across three review rounds, before the premise was reconsidered.)

Skipping is untouched by this phase. There are legitimate reasons to skip a test — debugger-only tests, the opt-in localization sweep, environment-gated tests — and they stay exactly as they are. A skip is simply not the answer to a test that started failing.

**Definition of done:** memory atom merged.

### P6 — Diagnostic noise
**Value: low · Effort: trivial · Risk: none.**

Bump `longRunningTestSeconds` in `tests/xunit.runner.json` to 10 (or remove). The 30s integration timeouts are intentional and the diagnostic floods drown real signal.

### P7 — Fixture mutation hygiene
**Value: low · Effort: trivial · Risk: low.**

Subsumed by P2a. The substitutes were single-instance because the factory was; the factory is now per test, so `ResetSharedSubstitutes` was deleted rather than kept — there is nothing left to share.

## Execution order

1. **P0** — fix the 53 integration failures. Bucket by cluster, one PR per cluster.
2. **P5** in parallel — write down that failures get fixed, so new ones don't reaccumulate.
3. **P1** — turn integration on in CI (only after P0 hits zero failures).
4. **P3** — Hangfire abstraction. Done before P2 so the shared-container fixture doesn't need to know about Hangfire.
5. **P2** — `IAssemblyFixture` migration.
6. **P4** — section-by-section in-memory-DB removal, in batches.
7. **P6, P7** — cleanup, opportunistic.

## Tracking

Parent: nobodies-collective/Humans#761. Phase issues:

| Phase | Issue |
|-------|-------|
| P0 — Fix 53 integration failures | nobodies-collective/Humans#762 |
| P1 — Integration in CI | nobodies-collective/Humans#763 |
| P2 — Shared container fixture | nobodies-collective/Humans#764 (shipped; isolation follow-up nobodies-collective/Humans#983) |
| P3 — Hangfire abstraction | nobodies-collective/Humans#765 |
| P4 — EF In-Memory migration | nobodies-collective/Humans#766 |
| P5 — Failures get fixed | nobodies-collective/Humans#767 |
| P6 — Diagnostic noise | nobodies-collective/Humans#768 |
| P2a — Per-test isolation | nobodies-collective/Humans#983 |
| P7 — Fixture mutation hygiene | nobodies-collective/Humans#769 (subsumed by P2a) |

Each phase issue carries `section:infra`. P4 sub-issues (per section batch) carry the relevant section label.

## Out of scope

- E2E (Playwright) tests under `tests/e2e/`. Separate substrate, different failure modes, different runner. Address separately if needed.
- Mutation testing (Stryker configs in `tests/Humans.Application.Tests/stryker-*.json`). Independent of the reliability problem.
- The Web.Tests project (27 files, all NSubstitute, no DB). Already healthy.
