<!-- freshness:triggers
  src/Sections/Humans.Permissions/**
-->
<!-- freshness:flag-on-change
  The rules in PermissionService (coordinator holds all, reaches down the tree, top-level only, inactive and system owners, admin roles) and the DI rule on owner kinds and sources.
-->

# Permissions — Section Invariants

Answers one question for the whole app: may this person do this on this owner?

## Concepts

- An **owner** (`Owner(Kind, Id)`, in Base) is what a role, task or permission belongs to: a `Team`, later a `Camp`. The owner's section registers its kind.
- A **permission** (`Permission`) is something a person may do on an owner, such as `Shifts.Manage`. The section that checks it declares it through `ISectionPermissions`. Its title and blurb are keys in the declaring section's resource set (`Resource`), so they show in every culture.
- An **owner kind** (`IOwnerKind`) gives the tree of owners of that kind: id, parent, active, system.
- A **permission source** (`IPermissionSource`) says what a person holds on which owners, and why: Roles, later Tasks, and for now Teams (today's coordinators, until departments move to Roles). A holding is either the owner's coordinator role (every permission) or a list of keys.
- A **system role** (Auth's roles: Admin, ShiftsAdmin, FinanceAdmin…) is tied to a section, not an owner. A permission lists the system roles that hold it on every owner (`AdminRoles`).

## Data Model

None. Nothing is stored: every answer is worked out from Auth's system roles and the sources' cached data.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Any section | Declares permissions; asks `IPermissionService` or authorizes with `PermissionRequirement`. Shifts declares and checks `Shifts.Manage` and `Shifts.Approve`; Budget declares and checks `Budget.Edit`; Expenses declares and checks `Expenses.Endorse`; Google declares and checks `Google.ManageResources`; Store declares and checks `Store.TeamOrders`; Teams declares and checks `Teams.Manage` and `Teams.EarlyEntry` |
| Owner sections (Teams) | Register an owner kind |
| Roles, later Tasks; Teams for now | Register a permission source |

## Invariants

- A permission key is declared by exactly one section; a second declaration fails the first check.
- Checking an undeclared key throws: it is a programming error.
- Admin, and every system role in a permission's `AdminRoles`, hold it on every owner of its kinds. No other system role does: TeamsAdmin holds only the permissions that list it.
- The coordinator role on an owner holds every permission that applies to that owner's kind, including ones declared later.
- A holding reaches the owner and every owner below it, never a parent or a sibling.
- A `TopLevelOnly` permission counts only holdings on a top-level owner.
- Holdings on inactive or system owners count for nothing, and nothing but an admin role grants anything on an inactive owner.
- Keys a source returns that no section declares are ignored.
- Owner kinds and sources are implemented on their sections' caching singletons and never inject `IPermissionService` or `IAuthorizationService`.

## Negative Access Rules

- A narrow holding **cannot** reach a parent or sibling owner.
- A system role **cannot** grant a permission it isn't listed for.

## Triggers

None. Permissions writes nothing; audit happens where a holding changes (Roles, Auth).

## Cross-Section Dependencies

- **Auth**: `ISystemRoleService.GetActiveForUserAsync` for the person's system roles.
- **Owner sections and sources**: found at runtime through DI (`IOwnerKind`, `IPermissionSource`); Permissions references none of them.

## Architecture

**Owning services:** `PermissionService` (an orchestrator)
**Owned tables:** None.
**Status:** (A) Migrated — new section, 2026-11-21.

### Public contract

| Public interface | Methods | Notes |
|---|---:|---|
| `IPermissionService` | 3 | `Permissions`, `HasAsync`, `GetOwnersAsync` |
| `ISectionPermissions`, `IOwnerKind`, `IPermissionSource` | — | Implemented by other sections |
| `PermissionRequirement` | — | Resource-based requirement; the resource is the `Owner`. Its handler resolves the cookie id to the surviving account first, so a merged-away id still works and a missing account is denied |

- No caching decorator: each check reads the sources' and Auth's caches.
- Tests: `tests/Humans.Permissions.Tests/PermissionServiceTests.cs` (the rules, table-driven).
