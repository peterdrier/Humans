# Agent — G0 First Audit

**Kind:** vertical · **Audited:** 2026-08-03 @ 5a9bbe198

## G1 — Ownership

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Every owned table read/written by exactly one repository | PASS | `reforge audit-downstream` on `AgentRepository`: all 12 methods touch only `AgentSettings`, `AgentConversations`, `AgentMessages`. No other repository references these DbSets (spot-checked via EF configs — no other section's config targets `agent_*` tables). |
| 2 | One writer-service per table | PASS | `AgentService` is the sole injector of `IAgentRepository` (`reforge injected IAgentRepository` scope limited to `Humans.Application.Services.Agent`). No interceptor workaround found. |
| 3 | No EF entity leaks across the boundary | PASS | `IAgentService`/`IAgentRepository` surfaces return `Agent*` DTOs/domain types consumed only within the section; no cross-section caller found injecting `IAgentRepository` or an Agent entity type. |
| 4 | No cross-section EF joins (zero baseline entries) | PASS | Grepped all 5 architecture-test baseline files (`ApplicationServiceEntityReadReturns`, `DisplaySortInControllers`, `NoDestructiveMigrationOps`, `NoLinqAtDbLayer`, `NoStartupGuards`) for `Agent` — zero hits. |
| 5 | No `[Obsolete]` navs / `[Grandfathered]` / baseline rows | PASS | `src/Humans.Infrastructure/Data/Configurations/Agent/*.cs` (3 files) — only internal nav is `AgentMessage.Conversation → AgentConversation`, both owned by Agent. No `[Grandfathered]` attribute anywhere under `*Agent*` in `src/`. |
| 6 | Controllers thin — no HUM0031 grandfathers | PASS | `AgentController.cs` — no `[Grandfathered]`/`[Obsolete]`. Body is orchestration + JSON shaping only (spot-checked). Minor smell (not a gate predicate): constructor injects `IUserServiceRead users` **and** `IUserServiceRead userService` — same interface twice under different parameter names; one is likely dead. Worth a one-line cleanup, not a gate blocker. |
| 7 | `docs/sections/Agent.md` current | **PARTIAL** | **Correction to this audit's earlier finding:** `docs/sections/Agent.md` DOES exist (a prior glob call with brace-expansion syntax against this directory returned a false negative — confirmed via `ls` and direct `Read`). The doc is extensive (169 lines: concepts, full data model, 11 invariants, tooling API, triggers, cross-section deps, architecture). **But** its "Architecture" section makes no mention of `AgentDbContext` (`src/Humans.Infrastructure/Data/AgentDbContext.cs`) — Agent already has its own **per-section `DbContext`**, `internal sealed`, mapping exactly `AgentConversations`/`AgentMessages`/`AgentSettings`, with its own migration history under `Migrations/Agent/` (issue #858, "Peel Agent" migration `20260715092843_PeelAgent`), registered in `AgentRepository`'s sole constructor. The doc instead describes the section as reading through `IAgentRepository` against the generic shared-DbContext pattern. This is the single most architecturally significant fact about the section for the Q3 ladder — Agent is functionally already past what G4 asks for — and it's missing from the doc. |

## G3 — Tests

| # | Predicate | Result | Evidence |
|---|-----------|--------|----------|
| 1 | Repository tests on real Postgres, zero EF-InMemory | **FAIL** | No dedicated `AgentRepositoryTests.cs` exists at all — `IAgentRepository` has zero direct repository-level test coverage against any database. |
| 2 | Service tests mock repo interfaces, zero `HumansDbContext` | **FAIL** | `AgentServiceTests.cs:556,578` and `AgentAdminStatusServiceTests.cs:111,173` both build a real `AgentRepository` backed by `UseInMemoryDatabase(...)` instead of mocking `IAgentRepository`. This is both the EF-InMemory violation and the "no HumansDbContext in service tests" violation in one. |
| 3 | Invariants/triggers from section doc each have a test | PASS (spot-check) | Using the real `docs/sections/Agent.md` (corrected per G1.7): "Enabled gate → 503" and "Rate limit → 429" invariants are plausibly covered by `AgentServiceTests.cs` given its breadth; "Refusal logging — every refused turn writes an AgentMessage with RefusalReason" is exercised at the rate-limit branch in `AgentService.AskAsync`; "Tool whitelist" and "Tool loop bound" invariants have direct coverage in `AgentToolDispatcherTests.cs`. Not exhaustively line-mapped. |
| 4 | No skipped tests without an issue ref | PASS | Grepped `tests/Humans.Application.Tests/Agent/**` for `Skip\s*=` — zero hits. |
| 5 | Tests grouped under the section | PASS | All Agent tests live under `tests/Humans.Application.Tests/Agent/` (16 files), movable as a unit. |

## G1 Gap List

1. **`docs/sections/Agent.md` doesn't document the dedicated `AgentDbContext`** (see G1.7). Fix: rewrite the "Architecture" section to describe the per-section context, its migration history, and flag to the Q3 tracker owner that Agent may already satisfy G4. No-migration-needed: y (docs only).
2. **Duplicate `IUserServiceRead` constructor parameter in `AgentController`** (`users` and `userService`, `src/Humans.Web/Controllers/AgentController.cs:19-23`). Cosmetic DI smell, not a boundary violation. Fix: drop the unused one. No-migration-needed: y.

## G3 Gap List (feeds G2 queue lightly, mostly pure G3 work)

1. **No `AgentRepositoryTests.cs`.** Add a repository test file against the shared Postgres fixture (#764 pattern) covering the 12 `IAgentRepository` methods. No-migration-needed: y.
2. **`AgentServiceTests.cs` and `AgentAdminStatusServiceTests.cs` construct a real `AgentRepository` over `UseInMemoryDatabase`** instead of `Mock<IAgentRepository>`. Convert both to interface mocks; this is exactly the #766 EF-InMemory-off conversion pattern. No-migration-needed: y.

## G2 Queue Notes

- Nothing schema-destructive spotted for Agent (no dead columns/tables flagged in this pass; would need a dedicated demolition-inventory sweep against `AgentSettings`/`AgentConversations`/`AgentMessages` columns to confirm).
- Table names already section-prefixed (`agent_*` per orchestrator-marker memory doc) — G2 rename item likely already satisfied for this section; verify at G2 entry.
- **Agent already has its own `DbContext` + migration history (issue #858)** — the section may already satisfy G4 outright. Recommend the tracker owner verify Agent against the formal G4 predicate list once written, rather than routing it through the G2→G4 turnstile queue like a from-scratch section.

## Orchestrator-marker check

`AgentService` implements `IAgentService : IApplicationService` (not `IOrchestrator`) and injects `IAgentRepository` directly — correctly classified as a **Section**, consistent with `memory/architecture/orchestrator-marker.md`'s explicit callout that the design-rules §15i "orchestrator" label on `AgentService` is wrong. No action needed; classification in the tracker should read "vertical", not "orchestrator".

## Verdict

**G1: 1 gap (docs/sections/Agent.md doesn't document AgentDbContext) · G3: 2 gaps (missing repo tests, EF-InMemory in service tests)**
