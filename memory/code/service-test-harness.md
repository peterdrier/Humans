---
name: Service tests inherit ServiceTestHarness
description: Service tests in Humans.Application.Tests should inherit ServiceTestHarness (Db, DbFactory, Clock, Cache, NewDbBackedUserService) instead of hand-rolling per-class scaffolding.
---

`tests/Humans.Application.Tests/Infrastructure/ServiceTestHarness.cs` is the base class for service tests that need an in-memory `HumansDbContext` + `FakeClock` + `IMemoryCache` + a DB-backed `IUserService` stub. Inherit it instead of repeating the constructor boilerplate.

**What the harness provides** (members are `private protected` — accessible from derived test classes within the test assembly):

- `Db` — the per-test in-memory `HumansDbContext`
- `DbFactory` — `TestDbContextFactory` over the same options (for repositories that take `IDbContextFactory<HumansDbContext>`)
- `DbOptions` — the underlying `DbContextOptions<HumansDbContext>`
- `Clock` — `FakeClock`, default `2026-03-01 12:00 UTC` (overrideable via the ctor)
- `Cache` — fresh `IMemoryCache`
- `NewDbBackedUserService()` — NSubstitute `IUserService` whose `GetByIdAsync` / `GetByIdsAsync` / `GetUserInfoAsync` / `GetUserInfosAsync` read from `Db`
- Seed helpers: `SeedUser`, `SeedTeam`, `SeedTeamMember`, `SeedRoleAssignment`, `SeedJoinRequest` — match the field-set the legacy local helpers had

The harness owns disposal of `Db` and `Cache`; do not write a `Dispose` in derived test classes unless you have extra resources.

**Why:** Before the harness, ~26 service test classes each open-coded the same 100–170-line constructor (DbContextOptionsBuilder + FakeClock + IServiceProvider + ~15 NSubstitute stubs + a 50-line `IUserService.GetByIdsAsync/GetUserInfosAsync/GetByIdAsync` lambda reading from the in-memory DB). The same `SeedUser`/`SeedTeam`/`SeedRoleAssignment` etc. were redeclared per file with subtly different signatures (`SeedUser` existed in 7 places). The duplication made tests harder to read and migrate; the harness collapses it.

**How to apply:**

1. New service test classes: `public sealed class FooServiceTests : ServiceTestHarness` — do not repeat the in-memory DB / clock / cache setup.
2. Use `Db`, `Clock`, `Cache`, `DbFactory` directly; do not declare local fields for these.
3. When the service-under-test needs `IUserService`, call `NewDbBackedUserService()`. **Capture to a local before passing into another `.Returns(...)` call** — see [[nsubstitute-no-nested-substitute-factories]].
4. When migrating an existing test class:
   - Drop the local `_dbContext` / `_clock` / `_cache` fields and their setup.
   - Drop the local `SeedUser` / `SeedTeam` / `SeedTeamMember` / `SeedRoleAssignment` / `SeedJoinRequest` if their signatures match the harness versions; keep test-class-specific seeders local but switch them from `_dbContext`/`_clock` to `Db`/`Clock`.
   - Rename usages: `_dbContext` → `Db`, `_clock` → `Clock`, `_cache` → `Cache`.
   - Replace the ~50-line in-DB `IUserService` lambda with `NewDbBackedUserService()` (captured into a local).
   - Change the class to `public sealed` (xUnit requires public test classes; `sealed` satisfies MA0053).

The TeamServiceTests migration is the worked example — its diff (-302 / +154 lines) shows the typical shape of a migration.

**What stays test-local:** Service construction (ctor wiring of the service-under-test and its substitute dependencies), service-specific seeders (e.g., `SeedEventSettings`, `SeedCampWithSeasonAsync`), and any test-specific cache-invalidator redirects.
