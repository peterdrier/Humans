---
name: authorization-conventions
description: "Controllers use `[Authorize(Policy = ...)]`: `<Section>Policies.Read` on the class, `.Manage` on every write; views use `authorize-policy`. Never `[Authorize(Roles=...)]` or `User.IsInRole`."
---

Authorization is policy-based end to end; `IAuthorizationService` is the single evaluator.

- **Routes:** `[Authorize(Policy = X)]`. Never `[Authorize(Roles = "...")]`, never `User.IsInRole(...)`
  chains for access decisions.
- **Board sees everything and changes nothing.** A section with its own admin role has a
  `<Section>Policies` class in its Contracts: `Read` (section admin, Board, Admin) on the controller
  class, nav items and tiles; `Manage` (section admin, Admin) on every POST, every create/edit form GET
  and every personal-data export. Board writes only in Governance, workgroups and legal documents.
  `BoardReadsEverythingTests` enforces both.
- **View visibility:** the `authorize-policy="X"` tag helper, e.g.
  `authorize-policy="@Humans.Teams.Contracts.TeamsPolicies.Manage"`. On a page Board can read, put
  `Manage` on each write form, create/edit link and export link.
- **View logic beyond visibility:** inject `IAuthorizationService` and call `AuthorizeAsync` —
  [`auth-in-views-self-resolving`](auth-in-views-self-resolving.md).
- **Resource-scoped auth** ("can this user edit *this* category?"):
  `AuthorizeAsync(User, resource, requirement)` with a registered requirement + handler pair; pattern in
  [`design-rules.md`, *Authorization*](../../docs/architecture/design-rules.md#authorization).
- **Services are auth-free.** No `isPrivileged` parameters; authorization happens before the call.
  One exception: full-Admin destructive deletes may add
  `IAdminAuthorizationService.RequireCurrentUserIsAdminAsync` inside the service as belt and braces; the
  route still carries `AdminOnly`.
- **Roles are already claims.** `RoleAssignmentClaimsTransformation` turns active role assignments into
  claims on every request from memory; don't query the DB for them.

**Where policies live:** a section's own names in `<Section>Policies` (its Contracts), registered by its
`Section.cs` through `ISectionPolicies`. `PolicyNames` in Base holds only the policies no single section
owns, registered in `src/Humans.Web/Authorization/AuthorizationPolicyExtensions.cs`.

**Why:** role strings scattered the auth vocabulary across three dialects, and auth inside services
caused startup dependency cycles.

**Related:** [`admin-role-superset`](admin-role-superset.md)
