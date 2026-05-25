# Test System Reliability

Rebuild the test setup so the suite is a reliable signal again — CI catches what local sees, integration tests survive concurrent runs, and "pre-existing failures on main, doesn't block the merge" stops being a sentence anyone says.

## Why this exists

PRs keep landing with the sentence *"the agent reported N pre-existing failures on `origin/main` unrelated to this PR — doesn't block the merge."* That happens because:

1. **CI does not run integration tests.** `.github/workflows/build.yml:36` filters them out (`--filter "FullyQualifiedName!~Integration"`). Integration failures only surface when someone runs locally, then get attributed to "pre-existing" and merged around.
2. **EF In-Memory** is used in 85 Application test files. It doesn't enforce FKs, NOT NULL, unique constraints, doesn't translate Npgsql LINQ, doesn't fire triggers — so unit tests pass while real-Postgres behavior diverges.
3. **Per-class Testcontainers Postgres.** ~18 integration test classes × `IClassFixture<HumansWebApplicationFactory>` × no parallelization control = up to 18 concurrent Postgres containers booting, each running all 96 migrations. Resource contention causes intermittent failures.
4. **Hangfire static state leakage.** `JobStorage.Current` is per-AppDomain. The codebase has six `if (!IsEnvironment("Testing"))` guards in `Program.cs` and infrastructure. Every new Hangfire-touching feature is one missed guard from breaking tests — this is the "Hangfire-init" failure cluster pattern.
5. **No quarantine discipline.** No allowlist, no `Skip=` policy linking to issues. Broken tests sit on `main` indefinitely.
6. Noise: `longRunningTestSeconds: 1` in `xunit.runner.json` floods integration runs with diagnostics, training the team to ignore xUnit output.

This is a horizontal — it doesn't belong to one section. Parent issue is `section:infra`. Child issues that fix a specific section's tests get that section's label.

## Phases

Each phase is one or more independent PRs off `main` (this is not `one-branch-for-phased-plans` — these are independently shippable; some span months). Order matters for P0–P3; P4 batches in parallel after P3.

### P0 — Fix the 53 existing integration test failures
**Value: high · Effort: medium · Risk: low. Tracking: nobodies-collective/Humans#762.**

Before turning anything on in CI, the existing failure backlog gets triaged. Each failure either:
- gets a fix PR (bucketed by cluster: Hangfire init, container race, schema drift, fixture state, etc.) — one PR per cluster, all linked to the P0 issue, **or**
- gets a `[HumansFact(Skip = "tracked-by: nobodies-collective/Humans#NNN")]` with a real follow-up issue.

No third option. The act of triage will surface the actual root causes (Hangfire is the prime suspect; container race is second). P1 stays blocked on P0 — turning CI green is non-negotiable.

**Definition of done:** `dotnet test tests/Humans.Integration.Tests` returns 0 failures on `origin/main` HEAD. Every skipped test has a tracking issue. P0 issue closes.

### P1 — Turn integration tests on in CI
**Value: high · Effort: small · Risk: low. Depends on P0.**

Remove `--filter "FullyQualifiedName!~Integration"` from `.github/workflows/build.yml:36`. Either run integration tests in the same job (simplest) or as a separate job with Postgres service container (cleaner separation, allows per-job timeout tuning). Keep the existing `--blame-hang-timeout 2m` guard.

**Definition of done:** integration tests run on every PR. A new "pre-existing failure" cannot land on `main` without being noticed.

### P2 — Share one Postgres container across the assembly
**Value: high · Effort: medium · Risk: medium. Depends on P0.**

Move from `IClassFixture<HumansWebApplicationFactory>` to `IAssemblyFixture<HumansWebApplicationFactory>` (xUnit v3 supports this natively). One container, one app boot, one migration pass per test run.

Per-test isolation via either:
- (a) `BEGIN; ROLLBACK` transaction wrapper per test — fast, clean for the common case.
- (b) `TRUNCATE TABLE ... CASCADE` reset between tests — necessary for tests that assert post-commit behavior (triggers firing, etc.).

Default to (a); tests that need post-commit assertions opt into (b) via a fixture base-class flag.

Tradeoff: shared container trades isolation for speed. Mitigated by per-test transaction rollback. A handful of tests will need the TRUNCATE variant — that's a 5-line fixture method.

**Definition of done:** integration suite boots one Postgres container per run; per-test isolation preserved; suite runtime drops to seconds, not minutes.

### P3 — Containerize Hangfire away from static state
**Value: high · Effort: medium · Risk: low. Can run in parallel with P2.**

`JobStorage.Current` and `RecurringJob.AddOrUpdate` are per-AppDomain statics. Wrap them behind interfaces (`IRecurringJobScheduler`, and the existing `IBackgroundJobClient`). Production binds to Hangfire; Testing binds to a no-op or an in-memory recorder substitute. Delete every `if (!IsEnvironment("Testing"))` guard in `Program.cs` and infrastructure.

For features that assert "the job was enqueued," verify via the abstraction substitute, not by inspecting Hangfire storage.

**Definition of done:** zero `IsEnvironment("Testing")` guards related to Hangfire in `src/`. Every feature that touches background jobs is testable without environment branching.

### P4 — Migrate Application repository tests off EF In-Memory
**Value: high · Effort: large · Risk: low. Depends on P2 (so the shared-fixture infra exists).**

85 files split into two camps:

- **Repository tests** (~30–40): they test LINQ translation. Must run against Postgres. Move onto the same shared-container fixture used by integration tests, or a slimmer per-assembly Postgres fixture inside `Humans.Application.Tests`.
- **Service tests using EF In-Memory as a stand-in for "any persistence"**: should not be touching `HumansDbContext` at all. Convert in place to mock the repository interface.

Ship **in batches by section** — one PR per section (Camps, Shifts, Events, Notifications, Profiles, Teams, Audit Log, Legal, Store, Tickets, Agent, …). Each batch is a `section:<name>` child issue with the appropriate section label. Side benefit: surfaces repositories that shouldn't have been reached through a DbContext in unit tests.

**Definition of done:** zero `.UseInMemoryDatabase(` references in `tests/`. Every section's tests run against real Postgres or against a mocked repository interface.

### P5 — Quarantine discipline
**Value: medium · Effort: small · Risk: low. Can run alongside P0.**

- Add `memory/process/no-pre-existing-failures.md` — failures on `main` block merges; the fix is to repair or `Skip` with an issue ref. *"Pre-existing, not my PR"* stops being a valid sentence.
- Lint rule: `[HumansFact(Skip = "...")]` skip string must contain `nobodies-collective/Humans#NNN`. Implement via a Roslyn analyzer or a CI grep step in `build.yml`.
- Weekly `/maintenance` sweep counts `Skip=` occurrences and surfaces oldest tracking issue.

**Definition of done:** memory atom merged; CI fails on a `Skip=` without an issue ref; `/maintenance` surfaces skipped tests.

### P6 — Diagnostic noise
**Value: low · Effort: trivial · Risk: none.**

Bump `longRunningTestSeconds` in `tests/xunit.runner.json` to 10 (or remove). The 30s integration timeouts are intentional and the diagnostic floods drown real signal.

### P7 — Fixture mutation hygiene
**Value: low · Effort: trivial · Risk: low.**

`HumansWebApplicationFactory.StripeServiceStub` and similar single-instance substitutes get reset in `IntegrationTestBase` (per-test `ClearSubstitute.For` calls) so any future move to method-level parallelism doesn't introduce flake.

## Execution order

1. **P0** — fix the 53 integration failures. Bucket by cluster, one PR per cluster.
2. **P5** in parallel — codify quarantine policy so new failures don't reaccumulate.
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
| P2 — Shared container fixture | nobodies-collective/Humans#764 |
| P3 — Hangfire abstraction | nobodies-collective/Humans#765 |
| P4 — EF In-Memory migration | nobodies-collective/Humans#766 |
| P5 — Quarantine discipline | nobodies-collective/Humans#767 |
| P6 — Diagnostic noise | nobodies-collective/Humans#768 |
| P7 — Fixture mutation hygiene | nobodies-collective/Humans#769 |

Each phase issue carries `section:infra`. P4 sub-issues (per section batch) carry the relevant section label.

## Out of scope

- E2E (Playwright) tests under `tests/e2e/`. Separate substrate, different failure modes, different runner. Address separately if needed.
- Mutation testing (Stryker configs in `tests/Humans.Application.Tests/stryker-*.json`). Independent of the reliability problem.
- The Web.Tests project (27 files, all NSubstitute, no DB). Already healthy.
