---
name: Narrow admin/console roles must join the AnyAdminRole policy
description: A new grantable narrow admin role (EarlyEntryArtAdmin, CantinaAdmin, …) must be added to the AnyAdminRole policy — the admin-shell entry gate — alongside RoleNames.All and (if board-grantable) BoardManageableRoles, or holders never see the ADMIN link.
---

When you introduce a new grantable **narrow** admin/console role (e.g. `EarlyEntryArtAdmin`, `CantinaAdmin`) that gates a single admin page, add it to the `AnyAdminRole` policy as well as to `RoleNames.All` and (if board-grantable) `BoardManageableRoles`.

**Why:** `AnyAdminRole` is the admin-shell entry gate — it controls whether the **ADMIN** link renders and whether the admin shell is reachable. A role-holder who is admitted only to their own page's policy will pass that page's `[Authorize]` but never see the link that leads there, so the nav item is unreachable. The page works only if you already know the URL.

**How to apply:** When wiring up the new role, register it in the `AnyAdminRole` policy (`src/Humans.Web/Authorization/AuthorizationPolicyExtensions.cs`) in addition to the role's own page policy.

Seen in commit f25d7e54.
