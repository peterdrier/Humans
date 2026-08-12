---
name: Authorization — policy-attribute, never raw role strings
description: All controller/route gates use `[Authorize(Policy = PolicyNames.X)]`. Views use `authorize-policy="X"` (tag helper) or inject `IAuthorizationService`. Never `[Authorize(Roles = "...")]`, never inline `User.IsInRole` chains for visibility checks.
---

Authorization in Humans is **policy-based, end to end** — controllers, views, and view components all speak the same `PolicyNames` vocabulary. ASP.NET Core's `IAuthorizationService` is the single evaluator.

**Rules:**

- **Controller / route gates:** `[Authorize(Policy = PolicyNames.X)]`. **Never** `[Authorize(Roles = "...")]` or `[Authorize(Roles = RoleGroups.X)]` — those are the legacy form and have all been migrated out.
- **View element visibility:** `authorize-policy="X"` tag helper attribute, e.g. `<li authorize-policy="IsActiveMember">`.
- **View conditional logic that needs more than visibility** (computing a flag, branching markup): `@inject IAuthorizationService AuthService` + `(await AuthService.AuthorizeAsync(User, PolicyNames.X)).Succeeded`. See [`auth-in-views-self-resolving`](auth-in-views-self-resolving.md).
- **No inline `User.IsInRole(...)` chains anywhere** for access decisions — they're the legacy form of the same check the policy already encodes.
- **Resource-scoped auth** (Can this user edit *this* budget category?): `IAuthorizationService.AuthorizeAsync(User, resource, requirement)` with a registered `*OperationRequirement` + `*AuthorizationHandler` pair. See [`design-rules.md §11`](../../docs/architecture/design-rules.md#11-authorization-pattern) for the full handler inventory.

**Where policies live:**

- `src/Humans.Web/Authorization/PolicyNames.cs` — string constants
- `src/Humans.Web/Authorization/AuthorizationPolicyExtensions.cs` — registration (`options.AddPolicy(PolicyNames.X, p => p.RequireRole(...))` or `.AddRequirements(...)`)

**Claims-first for global roles:** `RoleAssignmentClaimsTransformation` converts active `RoleAssignment` records into claims on every request (cached 60s). Don't query the DB for roles already available as claims.

**No `isPrivileged` booleans on service signatures.** Don't pass auth decisions as parameters to services. Services are auth-free — authorization happens before the service is called.

**Exception — full-Admin destructive deletes** may use `IAdminAuthorizationService.RequireCurrentUserIsAdminAsync` as belt-and-suspenders inside the service. The route still needs `[Authorize(Policy = PolicyNames.AdminOnly)]`. This exception is not for resource-scoped auth or ordinary edits.

**Why no Roles-attribute / no in-service auth:** see the Tombstone in [`design-rules.md §11`](../../docs/architecture/design-rules.md#11-authorization-pattern) — service-layer auth has produced two startup-cycle crashes (PR #210 / `225ac14`, `1626098`/`bbbe508`); raw `Roles = "..."` scattered auth vocabulary across three dialects (role strings, `ViewPolicies`, `RoleChecks`). The Phase-1 mechanical migration completed in 2026-05-27 (PR #808).

**Related:** [`admin-role-superset`](admin-role-superset.md), [`auth-in-views-self-resolving`](auth-in-views-self-resolving.md).
