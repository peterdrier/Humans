---
name: Authorization — policy-attribute, never raw role strings
description: Controllers use `[Authorize(Policy = ...)]` with a section's `<Section>Policies.Read` (Board reads) on the class and `.Manage` on every write; views use `authorize-policy`. Never `[Authorize(Roles=...)]` or `User.IsInRole` chains.
---

Authorization in Humans is **policy-based, end to end** — controllers, views, and view components all speak the same policy vocabulary. ASP.NET Core's `IAuthorizationService` is the single evaluator.

**Rules:**

- **Controller / route gates:** `[Authorize(Policy = X)]`. **Never** `[Authorize(Roles = "...")]` — those are the legacy form and have all been migrated out.
- **Board sees everything and changes nothing.** A section with its own admin role has a `<Section>Policies` class in its Contracts: `Read` (section admin, Board, Admin) on the class, nav items and tiles; `Manage` (section admin, Admin) on every POST, every create/edit form GET and every personal-data export. Board writes only in Governance, workgroups and legal documents. `BoardReadsEverythingTests` enforces both. `PolicyNames` in Base holds only the policies no one section owns.
- **View element visibility:** `authorize-policy="X"` tag helper attribute, e.g. `<li authorize-policy="AppAccess">`; a section policy is spelled `authorize-policy="@Humans.Teams.Contracts.TeamsPolicies.Manage"`. On a page Board can read, put `Manage` on each write form, create/edit link and export link.
- **View conditional logic that needs more than visibility** (computing a flag, branching markup): `@inject IAuthorizationService AuthService` + `(await AuthService.AuthorizeAsync(User, PolicyNames.X)).Succeeded`. See [`auth-in-views-self-resolving`](auth-in-views-self-resolving.md).
- **No inline `User.IsInRole(...)` chains anywhere** for access decisions — they're the legacy form of the same check the policy already encodes.
- **Resource-scoped auth** (Can this user edit *this* budget category?): `IAuthorizationService.AuthorizeAsync(User, resource, requirement)` with a registered `*OperationRequirement` + `*AuthorizationHandler` pair. See [`design-rules.md §11`](../../docs/architecture/design-rules.md#11-authorization-pattern) for the full handler inventory.

**Where policies live:**

- `<Section>Policies` in the section's Contracts — its own policy names, registered by its `Section.cs` through `ISectionPolicies`
- `src/Humans.Base/Authorization/PolicyNames.cs` — the cross-section names, registered in `src/Humans.Web/Authorization/AuthorizationPolicyExtensions.cs`

**Claims-first for global roles:** `RoleAssignmentClaimsTransformation` converts active `RoleAssignment` records into claims on every request (from in-memory caches, no DB hit). Don't query the DB for roles already available as claims.

**No `isPrivileged` booleans on service signatures.** Don't pass auth decisions as parameters to services. Services are auth-free — authorization happens before the service is called.

**Exception — full-Admin destructive deletes** may use `IAdminAuthorizationService.RequireCurrentUserIsAdminAsync` as belt-and-suspenders inside the service. The route still needs `[Authorize(Policy = PolicyNames.AdminOnly)]`. This exception is not for resource-scoped auth or ordinary edits.

**Why no Roles-attribute / no in-service auth:** see the Tombstone in [`design-rules.md §11`](../../docs/architecture/design-rules.md#11-authorization-pattern) — service-layer auth has produced two startup-cycle crashes (PR #210 / `225ac14`, `1626098`/`bbbe508`); raw `Roles = "..."` scattered auth vocabulary across three dialects (role strings, `ViewPolicies`, `RoleChecks`). The Phase-1 mechanical migration completed in 2026-05-27 (PR #808).

**Related:** [`admin-role-superset`](admin-role-superset.md), [`auth-in-views-self-resolving`](auth-in-views-self-resolving.md).
