---
name: GoogleResource is Teams-owned, repo Section tag notwithstanding
description: TeamResourceService directly injecting IGoogleResourceRepository is correct — the table is Teams-owned per design-rules and architecture tests; the repo's [Section("GoogleIntegration")] attribute is a stale label.
---

`TeamResourceService` (`Humans.Application.Services.Teams`) directly injects `IGoogleResourceRepository` and that is the correct shape. HUM0017 flags it as cross-section because the repository interface carries `[Section("GoogleIntegration")]`, but the underlying `google_resources` table is documented as Teams-owned (see `docs/sections/Teams.md` "Owned tables", `docs/architecture/design-rules.md`, and the `RepositoryOwners` map in `tests/Humans.Application.Tests/Architecture/ServiceBoundaryArchitectureTests.cs` which maps `IGoogleResourceRepository → "Teams"`). The `[Section("GoogleIntegration")]` tag is a stale label inherited from where the EF impl lives (`Humans.Infrastructure.Repositories.GoogleIntegration`).

`TeamResourceArchitectureTests` explicitly pins `TeamResourceService` taking `IGoogleResourceRepository` as a constructor parameter — flipping that to a service indirection breaks the pinned architecture invariant.

**Why:** The repository is the only path to `DbSet<GoogleResource>` (§15b) and `TeamResourceService` is the only writer. There is no separate `IGoogleResourceService` and there should not be one — `TeamResourceService` *is* that service. Introducing an intermediate service just to satisfy a section-label mismatch is the kind of pass-through wrapping `users-profiles-one-section.md` warns against.

**How to apply:** When HUM0017 fires on `TeamResourceService:IGoogleResourceRepository`, suppress with `#pragma warning disable HUM0017` and a reference to this atom — do not invent an `IGoogleResourceService` indirection. If retagging `IGoogleResourceRepository` to `[Section("Teams")]` later, expect `GoogleWorkspaceSyncService` (Services.GoogleIntegration, already in the cross-section ratchet baseline) to start firing HUM0017 — it stays suppressed there for the same reason: it's the legitimate cross-section reader, and the architecture-test baseline already records the exemption.

**Related:** `memory/architecture/users-profiles-one-section.md` (sibling rule for the Users/Profiles fold).
