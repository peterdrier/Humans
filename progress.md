# Codex Loop Progress

**Branch:** codex-loop/tech-debt
**Started:** 2026-03-21 15:51 UTC
**Prompt:** codex-tech-debt-prompt.md
**Phases:** 6
**Build:** dotnet build Humans.slnx && dotnet test Humans.slnx --filter "FullyQualifiedName~Application"

---

## Iteration 1 — Phase 1: Controller Pattern Consolidation
**Status:** success
**Summary:** Consolidated controller user/team access helpers and TempData messaging across feedback, shift admin, and related team controllers.
**Files:** src/Humans.Web/Authorization/ShiftRoleChecks.cs, src/Humans.Web/Controllers/AdminFeedbackController.cs, src/Humans.Web/Controllers/FeedbackController.cs, src/Humans.Web/Controllers/HumansTeamControllerBase.cs, src/Humans.Web/Controllers/ShiftAdminController.cs, src/Humans.Web/Controllers/TeamAdminController.cs, src/Humans.Web/Controllers/TeamController.cs
**Commit:** 1d7ace3

## Iteration 2 — Phase 1: Controller Pattern Consolidation
**Status:** success
**Summary:** Extracted shared camp controller access helpers and standardized CampController TempData messaging.
**Files:** src/Humans.Web/Controllers/CampController.cs, src/Humans.Web/Controllers/HumansCampControllerBase.cs, checkpoint.json
**Commit:** 3022a6d

## Iteration 3 — Phase 2: Service & Caching Consolidation
**Status:** success
**Summary:** Consolidated shared in-memory cache helpers and standardized camp contact rate-limit caching.
**Files:** src/Humans.Application/CacheKeys.cs, src/Humans.Application/Extensions/MemoryCacheExtensions.cs, src/Humans.Infrastructure/Services/OnboardingService.cs, src/Humans.Infrastructure/Services/ProfileService.cs, src/Humans.Infrastructure/Services/TeamService.cs, src/Humans.Web/Controllers/CampController.cs, checkpoint.json
**Commit:** 658b19f

## Iteration 4 — Phase 2: Service & Caching Consolidation
**Status:** success
**Summary:** Consolidated TeamService active-team cache mutations behind shared private helpers.
**Files:** src/Humans.Infrastructure/Services/TeamService.cs, checkpoint.json
**Commit:** 73d18ae

## Iteration 5 — Phase 2: Service & Caching Consolidation
**Status:** success
**Summary:** Centralized shared cache keys and cache invalidation helpers across services and auth caching.
**Files:** src/Humans.Application/CacheKeys.cs, src/Humans.Application/Extensions/MemoryCacheExtensions.cs, src/Humans.Infrastructure/Services/ApplicationDecisionService.cs, src/Humans.Infrastructure/Services/CampService.cs, src/Humans.Infrastructure/Services/OnboardingService.cs, src/Humans.Infrastructure/Services/ProfileService.cs, src/Humans.Infrastructure/Services/RoleAssignmentService.cs, src/Humans.Infrastructure/Services/ShiftManagementService.cs, src/Humans.Web/Authorization/RoleAssignmentClaimsTransformation.cs, checkpoint.json
**Commit:** 141aa29

## Iteration 6 — Phase 2: Service & Caching Consolidation
**Status:** success
**Summary:** Moved team directory summary/grouping into TeamService and backed it with the shared active-team cache.
**Files:** src/Humans.Application/Interfaces/ITeamService.cs, src/Humans.Infrastructure/Services/TeamService.cs, src/Humans.Web/Controllers/TeamController.cs, checkpoint.json
**Commit:** c7a9b83

## Iteration 7 — Phase 2: Service & Caching Consolidation
**Status:** success
**Summary:** Standardized shared cache invalidation helpers for active teams, nav badges, and camp season caches.
**Files:** src/Humans.Application/Extensions/MemoryCacheExtensions.cs, src/Humans.Infrastructure/Jobs/SystemTeamSyncJob.cs, src/Humans.Infrastructure/Services/CampService.cs, src/Humans.Infrastructure/Services/ProfileService.cs, checkpoint.json
**Commit:** a5da27d

## Iteration 8 — Phase 2: Service & Caching Consolidation
**Status:** success
**Summary:** Moved camp directory listing, filtering, and personalized listing assembly into CampService.
**Files:** src/Humans.Application/Interfaces/ICampService.cs, src/Humans.Infrastructure/Services/CampService.cs, src/Humans.Web/Controllers/CampController.cs, checkpoint.json
**Commit:** c0d06d7

## Iteration 9 — Phase 2: Service & Caching Consolidation
**Status:** success
**Summary:** Consolidated TeamService active-team cache mutations behind a single shared helper path.
**Files:** src/Humans.Infrastructure/Services/TeamService.cs, checkpoint.json
**Commit:** c5b396e

## Iteration 10 — Phase 2: Service & Caching Consolidation
**Status:** success
**Summary:** Centralized auth-cache invalidation for role and team-role mutations across services.
**Files:** src/Humans.Application/Extensions/MemoryCacheExtensions.cs, src/Humans.Infrastructure/Services/ProfileService.cs, src/Humans.Infrastructure/Services/RoleAssignmentService.cs, src/Humans.Infrastructure/Services/TeamService.cs, tests/Humans.Application.Tests/Services/ProfileServiceTests.cs, tests/Humans.Application.Tests/Services/RoleAssignmentServiceTests.cs, tests/Humans.Application.Tests/Services/TeamServiceTests.cs, checkpoint.json
**Commit:** ec098c3

## Iteration 11 — Phase 2: Service & Caching Consolidation
**Status:** success
**Summary:** Moved profile campaign-grant loading into ProfileService and centralized approved-profile cache mutations.
**Files:** src/Humans.Application/Extensions/MemoryCacheExtensions.cs, src/Humans.Application/Interfaces/IProfileService.cs, src/Humans.Infrastructure/Services/OnboardingService.cs, src/Humans.Infrastructure/Services/ProfileService.cs, src/Humans.Web/Controllers/ProfileController.cs, checkpoint.json
**Commit:** 4214d00

## Iteration 12 — Phase 2: Service & Caching Consolidation
**Status:** success
**Summary:** Consolidated approved-profile and user-access cache mutations behind shared memory-cache helpers.
**Files:** src/Humans.Application/Extensions/MemoryCacheExtensions.cs, src/Humans.Infrastructure/Services/OnboardingService.cs, src/Humans.Infrastructure/Services/ProfileService.cs, tests/Humans.Application.Tests/Services/ProfileServiceTests.cs, checkpoint.json
**Commit:** 0b585a9
