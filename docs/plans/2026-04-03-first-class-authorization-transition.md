# First-Class Authorization Transition Plan

**Goal:** Converge Humans on ASP.NET Core's built-in authorization system so that controllers, services, and views all speak the same language — and AI agents can implement features without first learning a custom auth layer.

**Current State (post PR #116):**
- Controllers use `[Authorize(Roles = "...")]` with comma-delimited `RoleGroups`/`RoleNames` strings
- Views use `authorize-policy="PolicyName"` backed by `ViewPolicies`, a custom `Dictionary<string, Func<ClaimsPrincipal, bool>>` that delegates to `RoleChecks`/`ShiftRoleChecks`
- Object-relative permissions (coordinator for department X, camp lead for camp Y) are handled ad-hoc with `if` statements in controllers
- No resource-based authorization handlers exist

**Target State:**
- Coarse section access uses named ASP.NET policies registered in startup
- Controllers use `[Authorize(Policy = "...")]` instead of raw role strings
- The `authorize-policy` TagHelper delegates to `IAuthorizationService` instead of `ViewPolicies`
- Object-relative rules use resource-based authorization handlers
- Sensitive mutations are enforced at service boundaries, not just controllers

**Why this matters at our scale:**
- Authorization rules exist in three dialects (role strings, RoleChecks methods, ViewPolicies names) — agents and humans have to keep all three in sync
- Every AI agent knows ASP.NET authorization out of the box; none know our custom layer without reading it first
- Resource-based auth is a real gap — scoped coordinator/lead permissions are approximated with coarse roles or ad-hoc checks that agents miss when implementing features

**Non-Goals:**
- Rewriting everything at once — each phase is one PR per section
- Encoding dynamic permissions into long-lived claims
- Changing business behavior during migration

---

## Phase 0: Inventory and Canonical Policy Names

**Objective:** Document current authorization behavior before changing anything. Produce a checked-in inventory that Phase 1 works from.

**Deliverables:** A single doc (e.g., `docs/authorization-inventory.md`) containing:

1. **Controller authorization map** — every `[Authorize(Roles = ...)]` grouped by section, with the controller, action, and role list
2. **View authorization map** — every `authorize-policy`, `RoleChecks.*`, and `Model.CanXxx` usage in views, grouped by section
3. **Same-rule-different-spelling table** — where controllers and views express the same rule in different ways (e.g., `RoleGroups.BoardOrAdmin` on a controller vs `ViewPolicies.AdminOrBoard` in a view)
4. **Enforcement gaps** — rules that are view-only today (button hidden but no server-side check) or server-only (protected endpoint with no corresponding UI gating)
5. **Canonical policy name table** — the final policy names that Phase 1 will register, mapped from current role strings and ViewPolicies names

**Exit criteria:**
- Every existing auth rule is accounted for
- Canonical names exist for all coarse-grained policies
- Gaps between UI visibility and server enforcement are explicitly listed
- **Creates the Phase 1 foundation issue** (register policies + rewire TagHelper)

---

## Phase 1: Migrate Coarse Policies to ASP.NET Authorization

**Objective:** Replace the three-dialect system with one. Register named ASP.NET policies, update controllers and the TagHelper, retire `ViewPolicies` and direct `RoleChecks` usage in views.

This is a mechanical refactor done section by section. Each section is one PR.

**What changes:**

1. **Register policies in startup** — each `ViewPolicies` entry becomes an `options.AddPolicy(...)` call using `RequireRole` or a custom requirement where needed (e.g., `IsActiveMember`, `ActiveMemberOrShiftAccess`)
2. **Update `AuthorizeViewTagHelper`** — replace `ViewPolicies.Evaluate(...)` with `IAuthorizationService.AuthorizeAsync(User, policyName)`. Fail closed for unknown policy names. Log unknown policies in development.
3. **Migrate controllers section by section** — replace `[Authorize(Roles = "Admin,Board")]` with `[Authorize(Policy = "AdminOrBoard")]` using the same registered policies
4. **Remove `ViewPolicies` and reduce `RoleChecks`** — once all consumers are migrated, delete the custom registry. `RoleChecks` can stay as internal helpers if policies need complex predicates, but nothing outside the authorization layer should call it directly.

**Section order** (one PR each, smallest risk first):
1. Shared layout + login partial (nav visibility)
2. Tickets
3. Finance
4. Volunteers / Shifts
5. Google / Admin
6. Teams / Camps
7. Board / Onboarding Review
8. Profiles

**Testing:**
- Unit tests for each registered policy (admin succeeds, regular member fails, etc.)
- Smoke test each section after migration to verify no behavior change
- Integration tests for protected routes where practical

**Exit criteria:**
- All `[Authorize(Roles = ...)]` replaced with `[Authorize(Policy = ...)]`
- TagHelper uses `IAuthorizationService`
- `ViewPolicies` deleted
- No direct `RoleChecks` usage outside the authorization registration layer

---

## Phase 2: Resource-Based Authorization (First Vertical Slice)

**Objective:** Prove the resource-based model with one domain that actually needs it. Do this when a feature demands it, not speculatively.

**Likely triggers:** department-scoped budget editing, scoped coordinator management, camp lead permissions.

**What changes:**

1. Define requirement + handler pairs for the chosen domain (e.g., `DepartmentBudgetOperations.Edit`)
2. Load the resource before authorization
3. Evaluate with `IAuthorizationService.AuthorizeAsync(User, resource, requirement)`
4. Mirror the same checks in views for button/link visibility
5. Coarse roles (Admin) still override as expected

**Example: department budget editing**
- Admin can edit any department budget
- FinanceAdmin can edit any department budget
- Department coordinator can edit their department's budget only
- Everyone else: denied

**Testing:**
- Unit tests per handler: allow, deny, override, null resource edge cases
- One end-to-end test: authorized user succeeds, unauthorized user gets 403, admin overrides

**Exit criteria:**
- One production scenario uses resource-based authorization end to end
- Coarse roles and scoped permissions work together in the same decision path

---

## Phase 3: Service-Layer Enforcement — **CANCELLED**

> **Status: tombstoned.** Do not reopen without reading this section. The superseding rule is `docs/architecture/design-rules.md` §11: services are auth-free.

**Original objective:** Push authorization checks down to service boundaries for sensitive mutations, so they're protected regardless of call path (controller, background job, future API).

**Why it was cancelled:**

1. **Phase 3a / #418 (role assignment, PR #210)** shipped and broke QA within two days. The cycle `TeamService → RoleAssignmentService → IAuthorizationService → TeamAuthorizationHandler → ITeamService` crashed DI validation on startup. Fixed in commit `225ac14` by making `TeamAuthorizationHandler` lazily resolve `ITeamService` via `IServiceProvider` — i.e., a service-locator escape hatch that hides the cycle from the validator rather than removing it. Hazard preserved, lesson ignored.
2. **Phase 3b / #419 (Google sync)** was merged as `1626098` and reverted in `bbbe508` for the same reason.
3. **Phase 3c / #420 (budget mutations)** was drafted on branch `sprint/20260415/batch-4` but never merged. Closed as *won't do* on 2026-04-15.

Two out of three attempts ended in crash or revert. The pattern has zero clean wins on this codebase.

**Why service-layer enforcement is unnecessary at our scale:**

The defence-in-depth argument for Phase 3 was "protect mutations regardless of call path — controllers, background jobs, future APIs." In practice on Humans:

- We have **one UI** and no public API. "Future API" is speculative; service-layer enforcement cannot be justified by a caller that does not exist.
- Background jobs that mutate state (`TicketingBudgetSyncJob`, `SystemTeamSyncJob`) are trusted server-side code and do not need to authenticate against their own domain's auth handlers.
- Controllers are the only human-facing mutation path. Enforcing auth there (via `[Authorize]` + resource-based `IAuthorizationService.AuthorizeAsync`) covers 100% of the real threat surface.

**The superseding pattern (from `design-rules.md` §11):**

```csharp
// Controller — authorize, then call service
var authResult = await _authorizationService.AuthorizeAsync(User, category, BudgetOperationRequirement.Edit);
if (!authResult.Succeeded) return Forbid();
await _budgetService.DeleteLineItemAsync(id);
```

Resource-based handlers (`BudgetAuthorizationHandler`, `RoleAssignmentAuthorizationHandler`, etc.) still exist and are still the right shape — they just get invoked from controllers, not from inside services. Services remain auth-free, do not inject `IAuthorizationService`, do not accept `ClaimsPrincipal` parameters, do not use `SystemPrincipal`.

**Unwind PR:** Phase 3a's in-service auth was reverted in a follow-up PR that removed the `ClaimsPrincipal` parameter from `IRoleAssignmentService`, moved the `AuthorizeAsync` call to `ProfileController`, deleted `SystemPrincipal`, and removed the `IServiceProvider` hack from `TeamAuthorizationHandler`. The `RoleAssignmentAuthorizationHandler` and `RoleAssignmentOperationRequirement` remain — they are now called from the controller, which is the correct pattern.

---

## Sequencing

| Phase | Trigger | Size |
|-------|---------|------|
| Phase 0 | PR #116 merged | 1 PR — inventory doc |
| Phase 1 | Phase 0 complete | ~8 section PRs, mechanical |
| Phase 2 | Feature needs object-relative auth | 1 PR for the vertical slice |
| ~~Phase 3~~ | **Cancelled — see Phase 3 section above** | — |

Phase 0 is the first issue created. Phase 1 is done section-by-section across sprint batches — not all at once. Each section PR is small and low-risk.

Phase 2 is driven by real feature work, not scheduled proactively. Phase 3 is cancelled; any future proposal to push auth into services must update `design-rules.md` §11 first.

---

## Issue Chain

Each phase step creates one GitHub issue. The exit gate of each issue includes creating the next issue in the chain, so sprint planning picks up one piece at a time.

**Phase 0 issue:**
0. Inventory current authorization usage and define canonical policy names

**Phase 1 issues (created sequentially, each exit gate creates the next):**
1. Register ASP.NET policies + rewire TagHelper to `IAuthorizationService`
2. Migrate shared layout + login partial
3. Migrate Tickets + Finance controllers
4. Migrate Volunteers + Shifts controllers
5. Migrate Google + Admin controllers
6. Migrate Teams + Camps controllers
7. Migrate Board + Onboarding Review + Profiles controllers
8. Remove `ViewPolicies`, reduce `RoleChecks` to internal helper

**Phase 2 issue:** created when a feature needs resource-based auth

**~~Phase 3 issues~~:** cancelled. #418 shipped and required hazard-hiding hotfix `225ac14`. #419 merged and reverted (`bbbe508`). #420 closed *won't do* on 2026-04-15. See the Phase 3 tombstone above for rationale.
