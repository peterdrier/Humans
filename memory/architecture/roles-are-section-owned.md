---
name: Roles are section-owned
description: A grantable role is a `<Section>Roles` constant declared through `ISectionRoles` in its owning section; Base's `RoleNames` holds only Admin and Board, and a rename keeps the old name in `FormerNames`.
---

Only `Admin` and `Board` live in `Humans.Base.Constants.RoleNames`. Every other role belongs to one section: the constant and its predicates sit in that section's Contracts (`CampsRoles.CampsAdmin`, `CampsRoles.IsCampsAdmin`), and `Section.cs` declares it through `ISectionRoles` as a `RoleDefinition` — name `<Section><Role>`, title, blurb, icon, `OpensAdminShell`, `FormerNames`. Auth composes the declarations into the `RoleCatalog` singleton, which every consumer (role assignment, Staff page, authorization pill, dev personas, Guide titles, `AnyAdminRole`) reads.

**Renaming a role:** change the constant, keep the old name in `FormerNames`, and leave the rest to the catalog — reads resolve former names until the operator runs the `auth.role-renames` data migration from `/Debug/DataMigrations`. Never remove a former name while any environment may still store it; `tests/Humans.Web.Tests/Sections/SectionRolesTests.cs` lists every name ever stored and fails when one stops resolving.

**Why:** a role named after its section is readable without a lookup, and a section that declares its own role can be added or retired without a Base or Shell edit. The former-name path exists because `role_assignments` rows outlive deploys and a lost name silently strips the role from everyone holding it.

**How to apply:** new role → constant in `<Section>Roles`, a `RoleDefinition` in `Section.cs`, a line in `ShippedStoredNames`. Cross-section checks use the owning section's predicate or a `PolicyNames` policy, never a hand-written role list. Related: [[narrow-admin-role-needs-anyadminrole]], [[section-contribution-seams]].
