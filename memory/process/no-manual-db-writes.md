---
name: No manual DB writes — any environment, any reason
description: HARD RULE. Never modify a database row by hand — no INSERT/UPDATE/DELETE via psql/admin UI, no `__EFMigrationsHistory` patching, no fix-up migrations to paper over regen mismatches. Code + EF migrations are the only ways state changes.
---

**HARD RULE.** Direct database writes are never allowed in any environment — production, QA, preview, or local-dev. This includes:

- Manual `INSERT` / `UPDATE` / `DELETE` via `psql`, Coolify's DB UI, pgAdmin, or any other ad-hoc tool
- Editing `__EFMigrationsHistory` to make recorded migration IDs match a regenerated migration (tempting after `dotnet ef migrations remove + add`)
- "Fix-up" no-op migrations whose only purpose is to rewrite history rather than express a real schema change
- Hand-editing data in seeded environments to set up test scenarios (use a dev seeder or `StubTicketVendorService`-style fake instead)

**Why:** Out-of-band writes desync code from data state. Migrations stop being a source of truth — any environment could be in any state, and the next migration may apply differently. Recovery becomes per-environment archeology. The whole point of EF migrations + Hangfire/dev seeders is *every* state change is in code, reviewable, and reproducible.

**How to apply:**

- Ticket / preview / QA env got into a bad state? **Drop and recreate the DB**, don't repair it. For preview envs, closing-and-reopening the PR triggers the GH Action that drops `humans_pr_{N}` and re-clones from QA.
- Migration regen left old environments with the old migration ID? **Drop the env's DB**. Don't update `__EFMigrationsHistory`. Don't write a no-op corrective migration.
- Need test data? Use `DevSeedController`, `StubTicketVendorService`, or add a real dev-seeder. Never `INSERT` rows directly.
- The exception is *normal application code paths* writing through repositories / services — that's not "manual DB writes", that's the application doing its job.

**Past incidents (this rule has been violated mentally — caught before action — multiple times in PR #421):**
1. Suggested manual SQL inserts to seed peter@nobodies.team's tickets in preview. Peter: "1 is NEVER allowed."
2. After EF migration regen broke preview env, suggested patching `__EFMigrationsHistory` or writing a fix-up migration. Peter: "2 is never allowed, you should know that. 3 - definitely not that either."

If reaching for any of the above patterns, **stop**. The right answer is always: drop and recreate the env, or write code/seeder/migration that legitimately produces the desired state.
