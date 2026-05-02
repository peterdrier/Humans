---
name: repository required for every DB-accessing service
description: HARD RULE. Every service that reads or writes a DB table goes through a repository interface; no service injects `HumansDbContext` directly, even for singleton-row tables.
---

If a service stores or reads anything in the database, it goes through a repository class. No exceptions — not for singleton-row settings tables, not for "trivial" lookups, not for "this is a tiny convenience" cases.

**Why:** The Application layer cannot reference EF types (`HumansDbContext`, `DbSet<T>`, `IQueryable`). The repository boundary is what keeps that rule enforceable: every DB call has a thick, materialized-list repository method behind it. The moment one service smuggles in `HumansDbContext` directly, the layer rule has a hole, and reviewers stop noticing the next one. Past instance: `AgentSettingsService` was originally written to inject `HumansDbContext` (singleton-row table, "why bother with a repo"). Refactored in `1f113b60` to use `IAgentRepository` and the rule snapped back into place.

**How to apply:** New service touches the DB → first define the repository interface in `Humans.Application/Interfaces/Repositories/I<Section>Repository.cs`, implement in `Humans.Infrastructure/Repositories/<Section>/<Section>Repository.cs`, then build the service on top. If the section has one settings row, the repository still exists — it has `GetSettingsAsync`/`SaveSettingsAsync` methods. If you find yourself writing `_db.Set<T>()` inside an Application service, stop and add a repository method instead. See also [`no-linq-at-db-layer`](no-linq-at-db-layer.md).
