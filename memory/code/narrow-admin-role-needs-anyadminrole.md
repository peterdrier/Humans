---
name: Narrow admin/console roles must join the AnyAdminRole policy
description: A new admin-shell role is declared in its section's `ISectionRoles`; `AnyAdminRole` derives from that, so set `OpensAdminShell: false` only when the role's page lives outside `/Admin`.
---

When you introduce a new grantable admin/console role (e.g. `CantinaAdmin`), declare it in the section's `Section.cs` through `ISectionRoles` with a `RoleDefinition`. `AnyAdminRole` is built from the catalog (every role with `OpensAdminShell`, the default), so the ADMIN link and the shell admit the holder without a Shell edit.

Set `OpensAdminShell: false` only for a role whose surface lives outside `/Admin` — e.g. `EarlyEntryAdmin`, whose page is reached from a team's own Details page — because its link does not depend on the ADMIN entry gate.

**Why:** `AnyAdminRole` is the admin-shell entry gate — it controls whether the **ADMIN** link renders and whether the admin shell is reachable. A role-holder who is admitted only to their own page's policy would pass that page's `[Authorize]` but never see the link that leads there. Deriving the gate from the declaration makes that mistake impossible.

**How to apply:** declare the role once, in `ISectionRoles`; never add a role list to `AuthorizationPolicyExtensions`. See [[roles-are-section-owned]].

Seen in commit f25d7e54; derived since the roles PR (2026-10).
