---
name: service-test-harness
description: Users' service tests inherit `ServiceTestHarness` (Db, Clock, Cache, `NewDbBackedUserService`) instead of hand-rolled scaffolding — other sections copy the shape locally.
---

Service tests inherit a harness instead of repeating in-memory DB, clock and cache setup.

- **Users:** `tests/Humans.Users.Tests/Infrastructure/ServiceTestHarness.cs`. It is built on the internal
  `UsersDbContext`, so no other test project can reference it.
- **Every other section:** its own trimmed `<Section>TestHarness` under
  `tests/Humans.<Section>.Tests/Infrastructure/` (e.g. `TeamsTestHarness`, `ShiftsTestHarness`), carrying
  only what that section's tests use. Match the shape; don't invent a new one.

**What the Users harness provides:**
- `Db`, `DbFactory`, `DbOptions` — the per-test in-memory context, a factory over the same options, and
  the options.
- `Clock` (`FakeClock`, overridable in the ctor) and a fresh `Cache`.
- `NewDbBackedUserService()` — a substitute `IUserService` whose read methods read from `Db`.
- Seeders: `SeedUser`, `SeedTeam`, `SeedTeamMember`, `SeedRoleAssignment`, `SeedJoinRequest`.
- `NewSectionDbOptions<TContext>()` for another section's context, and `RegisterSection<TContext>` to
  expose it lazily; `SaveAllAsync()` flushes every context the test touched.

The harness disposes `Db` and `Cache`; write a `Dispose` only for extra resources.

**How to apply:**
1. New class: `public sealed class FooServiceTests : ServiceTestHarness` (or the section's harness). No
   local fields for db, clock or cache.
2. Need `IUserService`? Call `NewDbBackedUserService()` and capture it to a local before passing it into
   another `.Returns(...)` — [`nsubstitute-no-nested-substitute-factories`](nsubstitute-no-nested-substitute-factories.md).
3. Stays test-local: constructing the service under test and its substitutes, service-specific seeders,
   cache-invalidator redirects.

**Why:** dozens of test classes each open-coded the same 100+ line constructor and redeclared the same
seeders with subtly different signatures.
