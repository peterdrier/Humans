# Analyzer & Architecture-Test Consolidation

**Status:** plan / in-progress · **Branch:** `chore/analyzer-consolidation` · **Date:** 2026-05-25
**Driving rule:** [`memory/architecture/universal-enforcement-over-per-section.md`](../../../memory/architecture/universal-enforcement-over-per-section.md)

## Goal

Collapse the per-section architecture enforcers (analyzers + `*ArchitectureTests.cs`) into **universal** enforcers derived from the hard rules, and delete the per-section instances they subsume. Executed as an **orchestrator + worker-agent swarm in two waves** (today), not a multi-week phased rollout.

Non-goal: inventing new rules. Every consolidation cites an existing source (`peters-hard-rules.md`, `design-rules.md`, a `memory/` atom, a `Rules/*.cs`, or a `HUM####` analyzer). Anything without a source is a **decision gate**, not a work item.

## Current state (main @ 9355223d6)

Analyzers present: HUM0001–0022, HUM0024 (HUM0023 absent). Active git pattern is "convert ratchet → analyzer" — done for concurrency (HUM0007), obsolete-nav (HUM0021), cross-section-EF-join (HUM0024), and **Notification DbSet writes (HUM0022)**.

**The fork in the road:** DbSet-write ownership is now enforced three different ways for the same rule —
- Notifications → analyzer `NotificationDbSetWriteAnalyzer` (HUM0022, hardcodes Notification DbSets + `NotificationRepository`)
- Events → ratchet test `Only_EventRepository_Writes_Event_DbSets`
- AuditLog → ratchet test `Only_AuditLogRepository_Writes_AuditLogEntries_DbSet`

Two live `SaveChangesInterceptor`s exist: `UserInfoSaveChangesInterceptor`, `LegalDocumentSaveChangesInterceptor`. `IOrchestrator` marker does **not** exist yet (the [`orchestrator-marker`](../../../memory/architecture/orchestrator-marker.md) atom is "to build").

~280 per-section test methods across 48 `*ArchitectureTests.cs` files; many duplicate the universal `Rules/*.cs` classes or HUM analyzers.

## Rule inventory

### Tier 1 — grounded (source + an enforcer exists or is mandated). Safe to consolidate.
| Rule | Source | Enforcer status |
|---|---|---|
| Only the owning repository writes its section's tables; one table → one repository | `peters-hard-rules`, [`repository-required-for-db-access`] | **inconsistent** (HUM0022 + 2 ratchet tests) → unify |
| No cross-section reach into data/logic | `peters-hard-rules` | partial: HUM0017 (repo inject), HUM0024 (EF join); writes + interceptors not unified |
| Orchestrators own no tables, inject no repository | `peters-hard-rules`, [`orchestrator-marker`] | **gap** (per-section tests only; marker + analyzer "to build") |
| Caching decorators talk only to inner | `peters-hard-rules`, [`decorators-talk-only-to-inner`] | HUM0020 |
| Repos sealed / in Infrastructure; services take no DbContext / no IMemoryCache | `Rules/*.cs` | universal already (per-section tests redundant) |
| Service/repo namespace; web no repo injection; obsolete-nav; concurrency; EF joins | HUM0012/13/14/21/07/24 | universal already (per-section tests redundant) |
| Grandfather/severity mechanism | [`analyzer-exceptions-via-attributes`] | `Error` + `[Grandfathered]`, no baselines/blanket-downgrade |

### Tier 2 — proposed (similar tests exist, NO governing rule). DECISION required before any work.
- **SaveChangesInterceptors** — are `UserInfoSaveChangesInterceptor` / `LegalDocumentSaveChangesInterceptor` legitimate crosscut mechanisms or cross-section-write debt to remove? ([`crosscut-purity`] is the lens.) Until ruled, do **not** ban interceptors.
- Append-only repositories (doctrine today is "only audit-log immutability is doctrinal"; consent uses DB triggers).
- Connector/SDK isolation as one rule (per-connector today).
- Info/View projections immutable-record convention.

### Tier 3 — claimed one-offs. Re-derive under the "can it be general?" test; keep only what survives.

## Execution — orchestrator + workers, two waves

Orchestrator = main session (Opus). Workers = `Agent` with `isolation: "worktree"`, branched off `chore/analyzer-consolidation`; mechanical work on Sonnet, analyzer-authoring on Opus (per [`model-tiering`]). One integration branch → **one draft PR** (per `no-direct-to-main`, one-branch-one-PR, `wip-prs-as-draft`). Orchestrator owns shared files (`AnalyzerReleases.Unshipped.md`, `Directory.Build.props`, `memory/INDEX.md`) and final `dotnet build`/`test`.

### Wave A — universal analyzers (analyzer project only; no test-file edits)
- **A1 — Universal cross-section-write analyzer.** Generalize HUM0022 into one analyzer keyed off the ownership map `CrossSectionEfJoinAnalyzer` (HUM0024) already builds (`DbSet → owning section`, `writer-type → its section`); fire on any EF write (`Add/AddRange/Update/Remove/Attach/...`) from a non-owning top-level type. `Error` + `[Grandfathered]`. Delete `NotificationDbSetWriteAnalyzer` + its test. (The Events/AuditLog ratchet-test deletions happen in Wave B, citing this analyzer.)
- **A2 — Orchestrator-no-repository analyzer.** Create `IOrchestrator` marker (sibling of `IApplicationService`), apply it to the orchestrators (definition from `CONTEXT.md`), build the analyzer (no `IRepository` ctor param, owns no DbSet). `Error` + `[Grandfathered]`. *Carries a labeling judgment — flagged.*

Gate between waves: A integrated, `dotnet build Humans.slnx -v quiet` green.

### Wave B — per-section test cleanup (test files only; partitioned by file group)
N Sonnet workers, each owning a disjoint set of `*ArchitectureTests.cs`. Per method: delete **iff** provably covered by a now-green universal enforcer (cite which: the `Rules/*.cs` class or `HUM####`); this includes the DbSet-write ratchet tests (cite A1) and orchestrator tests (cite A2). Leave section-specific / uncertain methods in place, each flagged in the PR body for Tier-3 review. No file is owned by two workers.

### Decision gates (parallel with Peter; block the items they name only)
Interceptor adjudication; Tier-2 generalizations; Tier-3 keep list. None block Wave A/B.

## Acceptance criteria
- One universal cross-section-write analyzer; `NotificationDbSetWriteAnalyzer` and the Events/AuditLog `Only_*Writes*` ratchet tests gone.
- New violations in either new analyzer emit `Error`; existing violators carry `[Grandfathered]`; no new `Directory.Build.props` blanket downgrade.
- `dotnet build` + `dotnet test` green; net test count down; no rule lost (every deleted test maps to a cited enforcer).
- `memory/INDEX.md` + the intention atom committed in this PR.

## Out of scope (this PR)
Interceptor removal, append-only/connector/immutable-record rules, and anything in Tier 2/3 pending a decision.
