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

## Phase 3: Service-Layer Enforcement

**Objective:** Push authorization checks down to service boundaries for sensitive mutations, so they're protected regardless of call path (controller, background job, future API).

**Do this incrementally** — as new mutation paths are added or existing ones are refactored.

**First candidates:**
- Role assignment / removal
- Google sync actions with external side effects
- Budget mutations
- Privileged onboarding/review actions

**What changes:**

- Service methods accept the current `ClaimsPrincipal` (or an authorization context) and call `IAuthorizationService` before executing
- Controllers become thin — they authorize via attribute and delegate to the service
- Services that are also called from background jobs use a system-level authorization context

**Exit criteria:**
- Sensitive workflows are protected even if later reused from jobs, APIs, or alternate UI surfaces
- Controllers don't duplicate authorization logic that services already enforce

---

## Sequencing

| Phase | Trigger | Size |
|-------|---------|------|
| Phase 0 | PR #116 merged | 1 PR — inventory doc |
| Phase 1 | Phase 0 complete | ~8 section PRs, mechanical |
| Phase 2 | Feature needs object-relative auth | 1 PR for the vertical slice |
| Phase 3 | Ongoing as mutations are added/refactored | Incremental |

Phase 0 is the first issue created. Phase 1 is done section-by-section across sprint batches — not all at once. Each section PR is small and low-risk.

Phases 2 and 3 are driven by real feature work, not scheduled proactively.

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

**Phase 3 issues:** created per-service as mutations are added
