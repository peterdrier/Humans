---
name: Authorization — centralized declarations and shared role-check helpers
description: Prefer `[Authorize(Roles = ...)]` with `RoleGroups`/`RoleNames` for static guards; `RoleChecks`/`ShiftRoleChecks` helpers for runtime combinations. No inline IsInRole chains.
---

Prefer centralized authorization declarations and shared role-check helpers over hand-written combinations.

**Rule:**
- Use `[Authorize(Roles = ...)]` with `RoleGroups`/`RoleNames` for static route guards
- Use shared `RoleChecks` / `ShiftRoleChecks` helpers for runtime combinations that cannot be expressed cleanly as an attribute
- Avoid repeating the same multi-role checks inline across multiple files

**Examples:**
- Use `RoleGroups.BoardOrAdmin`, not `"Board,Admin"`
- Use `ShiftRoleChecks.CanAccessDashboard(User)`, not repeated `IsInRole` chains

### Resource-Based Handlers

**Target pattern:** ASP.NET Core resource-based authorization via `IAuthorizationService.AuthorizeAsync(User, resource, requirement)`. See [`design-rules.md §11`](../../docs/architecture/design-rules.md#11-authorization-pattern) for the full rule and handler inventory.

**Current state (migrating):** Some controllers still use inline `IsInRole` checks or `RoleChecks`/`ShiftRoleChecks` helpers. These work but should migrate to authorization handlers over time. `[Authorize(Roles = ...)]` is still fine for simple route-level role gates.

**Claims-first for global roles** still applies during migration: `RoleAssignmentClaimsTransformation` converts active `RoleAssignment` records into claims on every request (cached 60s). Don't query the DB for roles already available as claims.

**No `isPrivileged` booleans.** Don't pass auth decisions as parameters to services. Services are auth-free — authorization happens before the service is called.

**Related:** [`admin-role-superset`](admin-role-superset.md).
