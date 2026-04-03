# First-Class Authorization Transition Plan

**Goal:** Move Humans from scattered role checks plus view-only authorization helpers to a single ASP.NET Core authorization system that supports both global role-based access and resource-based, object-relative permissions.

**Current State:** Controllers primarily use `[Authorize(Roles = ...)]`. Views currently mix inline `RoleChecks.*` calls and the new `authorize-policy` TagHelper backed by `ViewPolicies` in `src/Humans.Web/TagHelpers/AuthorizeViewTagHelper.cs`.

**Target State:** Authorization rules are defined once in ASP.NET Core authorization, enforced at controller and service boundaries, and reused in views/components via `IAuthorizationService` or a thin TagHelper wrapper. Object-relative rules such as "coordinator of department Foo can manage subteam Bar under Foo" are handled by resource-based authorization handlers.

**Why:** The current direction is an improvement for Razor maintainability, but it still leaves a split-brain model:

- controller protection and view visibility are not driven by the same policy definitions
- string-based `ViewPolicies` are a custom parallel framework
- object-relative rules do not fit cleanly in static role checks
- future domains like department budgets, subteam role management, and scoped coordinator rights need resource-aware authorization

**Non-Goals:**

- rewriting all authorization in one pass
- moving all authorization into views
- encoding dynamic object-relative permissions into long-lived claims
- changing business behavior during the initial migration phases

---

## Architectural End State

Humans should converge on three layers of authorization:

1. **Section access policies**
   Use named ASP.NET policies for coarse entry points such as Tickets, Finance, Google, Review Queue, and Volunteers.

2. **Resource-based authorization**
   Use requirement + handler pairs for object-relative decisions such as:
   - user can manage team roles for this team
   - user can edit this department budget
   - user can administer this camp season
   - user can approve this volunteer workflow item

3. **View visibility as a projection of real authorization**
   Razor decides whether to render actions by asking the same authorization system used by controllers and services. Views never become the enforcement boundary.

The intended rule is:

- controllers and services enforce access
- views and components reflect access
- one authorization system owns the rules

---

## Design Principles

### 1. Keep roles, but stop treating them as the full model

Existing roles like `Admin`, `Board`, `TeamsAdmin`, `VolunteerCoordinator`, and `FinanceAdmin` remain valuable. They should continue to express broad privileges.

But roles alone are insufficient for rules like:

- Amy coordinates department Foo, so she can edit Foo budget but not Bar budget
- a coordinator can manage subteams within their department tree
- a camp admin can manage only their assigned camp resources

Those rules should be evaluated against the specific domain object.

### 2. Prefer framework policies over custom view registries

The `authorize-policy` syntax is fine ergonomically. The improvement needed is under the hood:

- today: `authorize-policy` delegates to `ViewPolicies.Evaluate(...)`
- target: `authorize-policy` delegates to `IAuthorizationService`

This preserves clean Razor while removing the custom policy engine.

### 3. Do not rely on claims for fast-changing object permissions

Stable broad privileges can live in roles and claims.

Dynamic scoped permissions like "current coordinator of department Foo" should normally be loaded from application data during authorization. Otherwise the system will drift toward stale claims and cache invalidation problems.

### 4. Enforce at mutation boundaries

Any action that changes data or reveals privileged data must be protected outside the view layer:

- controller action
- application service
- domain workflow service

Views should only hide links and buttons, never be the sole control.

---

## Policy Model

Humans should define two kinds of policies.

### A. Named coarse-grained policies

Examples:

- `CanAccessTickets`
- `CanManageTickets`
- `CanAccessFinance`
- `CanAccessReviewQueue`
- `CanAccessVolunteers`
- `AdminOrBoard`
- `HumanAdminBoardOrAdmin`

These mostly map to existing `RoleChecks` and `ShiftRoleChecks` behavior, but they become first-class ASP.NET policies registered in startup.

### B. Resource-based operations

Examples:

- `TeamOperations.Edit`
- `TeamOperations.ManageRoles`
- `DepartmentOperations.ManageSubteams`
- `DepartmentBudgetOperations.View`
- `DepartmentBudgetOperations.Edit`
- `CampOperations.Manage`

These should be implemented as requirements and handlers evaluated against the actual resource instance.

---

## Proposed Migration Phases

## Phase 0: Inventory and Lock the Baseline

**Objective:** Document current behavior before changing the underlying authorization mechanism.

**Deliverables:**

- inventory of current `[Authorize(Roles = ...)]` usage
- inventory of `RoleChecks.*` and `ShiftRoleChecks.*` usage in views, view components, and controllers
- grouped map of "same rule, different spelling" across the codebase
- decision log for canonical policy names

**Tasks:**

- scan controllers for role attributes and classify by section
- scan Razor for inline authorization checks and `authorize-policy`
- identify rules that are view-only today and verify whether they are also enforced server-side
- identify rules that are actually object-relative but currently approximated by coarse roles

**Exit Criteria:**

- every existing auth rule is accounted for
- canonical names exist for coarse-grained policies
- gaps between UI visibility and actual enforcement are explicitly listed

---

## Phase 1: Introduce First-Class ASP.NET Policies Without Behavior Change

**Objective:** Register named ASP.NET policies that mirror existing behavior exactly.

**Deliverables:**

- `AddAuthorization(...)` policy registration for all existing coarse rules
- shared policy naming conventions
- no behavior change at endpoints or in views yet

**Tasks:**

- create policy registration module for web authorization
- implement policies using current `RoleChecks` / `ShiftRoleChecks` predicates
- keep policy names aligned with current semantics, not aspirational future semantics
- document each policy with the source rule it replaces

**Recommended Scope:**

- start with rules already represented in `ViewPolicies`
- include controller-oriented policies for the same areas

**Exit Criteria:**

- coarse policies exist in the framework
- each mapped policy has tests proving parity with current role-check behavior

---

## Phase 2: Rewire the TagHelper to the Real Authorization System

**Objective:** Keep the Razor ergonomics, but replace `ViewPolicies` as the evaluator.

**Deliverables:**

- `authorize-policy` TagHelper using `IAuthorizationService`
- optional temporary compatibility shim while migrating names
- removal plan for `ViewPolicies`

**Tasks:**

- update `AuthorizeViewTagHelper` to call `AuthorizeAsync(User, policyName)`
- fail closed for missing policies
- keep output suppression behavior the same
- add logging for unknown or failed policy resolution during development

**Important Rule:**

The TagHelper remains a view convenience, not the place where policy logic lives.

**Exit Criteria:**

- Razor visibility now runs through ASP.NET policies
- existing pages render the same for the tested role matrix
- `ViewPolicies` is either removed or reduced to a short-lived compatibility layer

---

## Phase 3: Migrate Controllers From Role Attributes to Policy Attributes

**Objective:** Eliminate dual definitions where controllers speak roles and views speak policies.

**Deliverables:**

- controller actions using `[Authorize(Policy = "...")]` where appropriate
- parity tests for route protection

**Tasks:**

- migrate section by section, not file by file
- prefer replacing one full section at a time: Tickets, Finance, Volunteers, Google, Camps
- preserve existing semantics before improving them
- leave genuinely custom cases alone until resource-based handlers exist

**Good Candidates For Early Migration:**

- Tickets
- Finance
- Review Queue
- Volunteers
- Admin / Board split pages

**Exit Criteria:**

- coarse section access no longer depends on duplicated role lists in controllers
- named policies are the canonical expression at the web boundary

---

## Phase 4: Introduce Resource-Based Authorization in One Vertical Slice

**Objective:** Prove the resource-based model with one domain that actually needs it.

**Recommended Pilot:** Department-scoped management.

Good examples:

- coordinator for department Foo can manage subteams under Foo
- coordinator for department Foo can edit Foo's own budget
- admin can always override

**Deliverables:**

- requirement + handler set for one domain slice
- application/service enforcement
- matching UI visibility

**Tasks:**

- choose one bounded scenario with real object-relative rules
- define resource model and operations explicitly
- load the relevant resource before authorization
- evaluate authorization using `IAuthorizationService.AuthorizeAsync(User, resource, requirement)`
- mirror the same checks in views for buttons and links

**Why This Matters:**

This phase is where Humans stops being "roles plus nicer view syntax" and becomes a real authorization system.

**Exit Criteria:**

- one production scenario uses resource-based authorization end to end
- coarse roles and scoped permissions work together in one decision path

---

## Phase 5: Move Critical Authorization Into Application Services

**Objective:** Ensure mutations remain protected even if new call paths are added later.

**Deliverables:**

- service methods that authorize sensitive operations before executing
- explicit authorization context passed into workflows where needed

**Tasks:**

- identify high-risk mutation paths
- add authorization checks to service boundaries for privileged actions
- keep controllers thin and avoid letting authorization live only at the MVC edge

**Likely First Candidates:**

- role assignment / removal
- department or budget management
- sync actions with external side effects
- privileged onboarding or review actions

**Exit Criteria:**

- sensitive workflows are protected even if later reused from jobs, APIs, or alternate UI surfaces

---

## Phase 6: Remove Legacy Authorization Paths

**Objective:** Finish the migration and prevent regressions back to the old pattern.

**Deliverables:**

- no new direct `RoleChecks.*` in Razor except rare deliberate exceptions
- no remaining custom `ViewPolicies` registry
- clear team guidance in docs/architecture

**Tasks:**

- delete obsolete compatibility code
- add guardrails to code review guidance and architecture docs
- optionally add linting or CI checks for direct role checks in views

**Exit Criteria:**

- authorization is centralized
- migration guidance is documented
- old patterns are mechanically discouraged

---

## Testing Strategy

This migration should not rely on manual spot checks alone.

## 1. Unit Tests For Policy Parity

Add focused tests for named policies that mirror current coarse-grained behavior.

Examples:

- `AdminOrBoard` succeeds for Admin and Board, fails otherwise
- `CanManageTickets` succeeds for Admin and TicketAdmin, fails for Board if that is current behavior
- `ActiveMemberOrShiftAccess` matches the current active-member plus shift-dashboard logic

These are the fast safety net for Phases 1 to 3.

## 2. Unit Tests For Resource-Based Handlers

For each handler:

- allow cases
- deny cases
- override cases
- null or missing relationship cases
- archived / disabled / inactive edge cases

Example matrix for department budget editing:

- Admin can edit any department budget
- FinanceAdmin can edit according to policy decision
- VolunteerCoordinator for Foo can edit Foo budget
- VolunteerCoordinator for Foo cannot edit Bar budget
- former coordinator cannot edit after revocation

## 3. Integration Tests For Protected Endpoints

Use web/integration tests to verify:

- authorized user gets `200`
- unauthorized user gets `403` or redirect as expected
- section routes still behave correctly after role attribute to policy migration

This is especially important when replacing `[Authorize(Roles = ...)]` with policies.

## 4. Razor / UI Visibility Tests

Add targeted rendering or end-to-end tests for nav and major action surfaces:

- main nav
- user dropdown
- section action buttons
- sensitive admin-only controls

These do not replace server protection, but they catch drift between enforcement and UX.

## 5. Vertical Slice End-to-End Tests

For the first resource-based domain:

- positive scenario
- negative scenario
- override scenario
- UI visibility plus POST/submit enforcement

If the app already has Playwright or section-based smoke coverage, extend that suite rather than creating a parallel UI test harness.

## 6. Regression Test Matrix

Maintain a compact role/resource matrix for the migration:

- anonymous
- active member
- TeamsAdmin
- VolunteerCoordinator
- TicketAdmin
- FinanceAdmin
- Board
- Admin
- scoped coordinator for Foo
- scoped coordinator for Foo trying to act on Bar

This matrix should drive both policy tests and review checklists.

---

## Rollout and Risk Management

## Behavior Preservation First

For Phases 1 to 3, the default expectation is no behavior change. If a policy is being renamed or rationalized, preserve semantics first and improve semantics later.

## Section-by-Section Rollout

Do not migrate the entire site at once. Migrate one area at a time and ship between phases.

Recommended order:

1. shared coarse policies
2. view TagHelper integration
3. Tickets / Google / Volunteers style sections
4. one scoped resource domain
5. service-layer enforcement

## Observability

Useful short-term instrumentation:

- debug logging on policy failures during development
- optional logging for unknown policy names in the TagHelper
- audit entries for sensitive resource-based decisions where relevant

## Fail Closed

Unknown policy names or missing handlers must deny access by default.

---

## Suggested GitHub Issue Breakdown

The following issue set is sized for normal workstreams and parallel execution.

### Issue 1: Inventory current authorization usage and define canonical policy names

**Outcome:** A checked-in inventory plus policy naming table.

### Issue 2: Register first-class ASP.NET policies for existing coarse-grained rules

**Outcome:** Startup registration and parity tests for current role semantics.

### Issue 3: Rework `authorize-policy` TagHelper to use `IAuthorizationService`

**Outcome:** Views use first-class policies without changing markup style.

### Issue 4: Migrate shared layout and common partials to policy-backed authorization

**Outcome:** `_Layout`, login partials, and other shared surfaces are on the new system.

### Issue 5: Migrate controller authorization from role lists to named policies in one section

**Suggested first section:** Tickets or Volunteers.

### Issue 6: Add integration tests for policy-protected routes

**Outcome:** Route-level regression coverage for migrated sections.

### Issue 7: Design resource-based authorization model for department-scoped permissions

**Outcome:** Requirements, handlers, operation names, and domain loading approach.

### Issue 8: Implement department-scoped authorization for one pilot workflow

**Suggested pilot:** department budget edit or subteam role management.

### Issue 9: Add end-to-end tests for the first resource-based workflow

**Outcome:** Positive, negative, and override coverage from UI to enforcement.

### Issue 10: Move authorization checks into application services for selected privileged workflows

**Outcome:** Mutation boundaries protected outside controllers.

### Issue 11: Remove `ViewPolicies` compatibility layer and document the new standard

**Outcome:** Old view-only policy engine retired, docs updated.

---

## Recommended Sequencing For Workstreams

If these become issues, a sensible dependency order is:

1. Issue 1
2. Issue 2
3. Issue 3
4. Issue 4 and Issue 5 in parallel once Issue 3 lands
5. Issue 6 alongside Issue 5
6. Issue 7
7. Issue 8
8. Issue 9 and Issue 10
9. Issue 11

This keeps the risky semantic shift, resource-based auth, after the low-risk consolidation work.

---

## Completion Criteria

The transition should be considered complete when all of the following are true:

- coarse section access uses ASP.NET policies as the source of truth
- views query the same authorization system as controllers
- at least one real domain uses resource-based authorization for object-relative permissions
- sensitive mutations are enforced at controller and/or service boundaries, not only reflected in views
- legacy view-only policy infrastructure has been removed
- automated tests cover policy parity, endpoint protection, and one resource-based vertical slice

---

## Recommended Follow-Up Docs

Once implementation starts, add or update:

- `docs/architecture.md` with the new authorization standard
- relevant section docs in `docs/sections/`
- a short developer guide showing:
  - when to use named policy vs resource-based authorization
  - where policy definitions live
  - how to add a new handler
  - how views should ask for authorization

