# Teams — Target Shape

Derived fresh each section-doctor run, before any scan. History rows at the bottom.

## 1. What the section does

Teams is how humans organise around a shared purpose. A team is a **department** (top
level) or a **sub-team** under exactly one department; each has members, optional named
role slots, an optional Google Group plus Drive folder, and — for departments — an
optional public page. A human joins an open team at once or asks to join an
approval-required one; a coordinator answers. A few **system teams** (Volunteers,
Coordinators, Board, Asociados, Colaboradors, Barrio Leads) are computed hourly from
other sections' facts and cannot be joined or left by hand. **Hidden** teams exist for
privacy-sensitive groupings; only admins see them. A team may also be switched on for
**Early Entry**, letting its coordinators put named crew on the festival's early-arrival
roster.

Every membership change made one at a time is audit-logged, mirrored to the team's Google
resources, and reflected in the Coordinators system team when a management role is involved.
Deactivating a team closes its memberships in bulk, audits each closure and reconciles its department
coordinators’ system-team membership. Google resources stay active until reconciliation revokes access.

## 2. The shapes

| Question shape | Asked by | Answered by |
|---|---|---|
| "Which teams exist / what is team X?" (whole graph, by id, by slug, with parents, name fan-out, search) | almost every section, Shell tiles, global search, the auth handler | `ITeamService.GetTeamsAsync` / `GetTeamAsync` / `GetTeamBySlugAsync` / `GetTeamsWithParentsAsync` / `SearchAsync`; `IEntityNameContributor` |
| "What may this human do on this team?" (coordinator of, coordinated ids, budget-scoped ids) | Shifts, Expenses, GoogleIntegration, `TeamAuthorizationHandler` | `ITeamService.IsUserCoordinatorOfTeamAsync` / `GetUserCoordinatedTeamIdsAsync` / `GetEffectiveBudgetCoordinatorTeamIdsAsync`; `TeamCoordinatorAccess` |
| "Which teams is this human in?" | Users, GoogleIntegration, the profile card | `ITeamService.GetUserTeamMembershipsAsync` |
| "Show me the directory / the tree / a team page / my teams / the roster / the summary" | the section's own controllers | `ITeamPageService.GetTeamDirectoryAsync` / `GetTeamPageDetailAsync` / `GetMyTeamMembershipsAsync` / `GetRosterAsync`; `ITeamManagementService.GetAdminTeamListAsync` |
| "Send this member a message as my team" | coordinators, from another member's profile | `ITeamMessageOptionsProvider`; `TeamMessageController`; Email's facilitated-message factory |
| "Let me in / let me out" (join, request, withdraw, leave; approve, reject) | members and coordinators via the controllers | `JoinTeamAsync` / `LeaveTeamAsync` / `WithdrawJoinRequestAsync` / `ApproveJoinRequestAsync` / `RejectJoinRequestAsync` + the two pending-request reads |
| "Shape the team" (create, edit, deactivate, page content, Google group prefix) | TeamsAdmin/Admin via the controllers (Board reads); GoogleIntegration for the prefix | `CreateTeamWithGoogleGroupAsync` / `UpdateTeamWithGoogleGroupAsync` / `DeleteTeamAsync` / `UpdateTeamPageContentAsync`; `ITeamWrite.SetGoogleGroupPrefixAsync` |
| "Staff the team" (members, role definitions, role assignments) | coordinators via the controllers | `AddMemberToTeamAsync` / `RemoveMemberAsync`; the role-definition members; `AssignToRoleAsync` / `UnassignFromRoleAsync` |
| "Who gets in early, for what?" | the per-team EE page; the EE roster | the EE-grant members; `IEarlyEntryProvider.GetEarlyEntriesAsync` |
| "Recompute the system teams" | Hangfire, the Google sync admin page, other sections' provisioning flows | `ISystemTeamSync` (job) over `ITeamWrite.ApplySystemTeamMembershipDeltaAsync` and the management interface's role-reconciliation members |
| "This human is gone / merged" | Users (deletion, merge), Gdpr | `RevokeAllMembershipsAsync`; `IUserMerge.ReassignAsync`; `IUserDataContributor` (export + erasure, via the decorator, which evicts) |
| "Seed a fixture" | Development, Budget dev seeders | `ITeamSeeding` |

Vocabulary the leaf carries: `TeamInfo` (+ `TeamMemberInfo`, `TeamRoleDefinitionSnapshot`,
`TeamRoleAssignmentSnapshot`), `UserTeamMembershipInfo`, `TeamSearchHit`, `SyncReport`,
`TeamMemberRole`, `RolePeriod`, `SlotPriority`, `TeamJoinRequestStatus`, `CallToAction` /
`CallToActionStyle`, `TeamCoordinatorAccess`.

## 3. Structure

The shapes imply:

- **A contracts leaf** with exactly the cross-section half: the read interface (the
  whole-graph, authority and membership shapes), a write interface carrying only members some *other* section
  calls, the system-sync contract, the seeding contract,
  and the flat records those signatures name. A member with no caller outside the
  assembly does not belong on the leaf.
- **One inner service facade** behind the internal management interface. It holds no rules of
  its own: each shape's workflow lives in one internal owner it constructs (lifecycle, joining,
  membership administration, role definitions, page content, early entry, data export, Google
  group memberships), sharing one snapshot reader for team, permission and canonical-user reads
  and one membership outbox for the atomic membership-plus-Google-event write. Every owner talks
  only to `ITeamRepository` for its own tables and to other sections through their interfaces.
- **One singleton caching decorator** over the same interface holding the canonical
  Teams-owned graph, resolving member display data live through the inner service, and clearing wholesale on every
  write so the next read re-warms the whole graph — no per-mutation diffing. Pending-request
  reads bypass it on purpose.
- **One page composer** (`ITeamPageService`) that builds the directory, team detail, My Teams and
  roster pages from the cached graph and role checks; it owns no tables and calls no repository.
- **One repository** with thick, named methods; no LINQ leaks.
- **Controllers**: member-facing (`/Teams/*`), per-team management
  (`/Teams/{slug}/*`) and send-as-team, all thin; the per-team ones share one
  resolve-slug-then-authorise base. The resource-based handler plus its requirement live beside them.
- **One system-team reconciler** (the hourly job) that reads other sections' facts and
  applies deltas through the service, never the repository.
- **Pure helpers** for the directory grouping, the page summary, the EE projection, slug
  resolution — static, tested in isolation, no I/O.
- **The metrics gauge loop**, the section's seams (`Section.cs`), views, one resx set.

## 4. Invariants

- At most one management role per team (`TeamRoleDefinitions.cs:115`, `TeamRoleDefinitions.cs:196`);
  only TeamsAdmin/Admin may set or clear the flag (`TeamAdminController.cs:462`, and the edit
  path keeps the stored flag for anyone else, `TeamRoleDefinitions.cs:96`).
- A coordinator of a department has authority over its sub-teams; a sub-team manager has none
  over the department or its siblings (`TeamCoordinatorAccess.cs:11`).
- Manual add, remove, join and leave on a system team are refused (`TeamMembershipAdministration.cs:34`,
  `TeamMembershipAdministration.cs:74`, `TeamJoining.cs:51`); role assignment on a system team never
  adds a non-member (`TeamRoleDefinitions.cs:285`).
- One pending request per human per team (`TeamJoining.cs:72`); only a pending request can be
  reviewed (`TeamJoining.cs:188`).
- Removing a member removes all their role assignments on that team (`TeamRepository.cs:592`).
- Hidden teams are left out of every non-admin listing the directory builds (`TeamDirectoryBuilder.cs:83`).
- Only departments can have a public page (`TeamPageContent.cs:46`).
- `IsSensitive` is written only by a global Admin, on a change to the flag (`TeamLifecycle.cs:220`).
- An early-entry grant may be created only while its team has `EarlyEntryEnabled` (`TeamEarlyEntry.cs:36`).
- Slugs resolve on `Slug` or `CustomSlug`, case-insensitively (`TeamSlugResolver.cs:11`).
- Erasure ends live memberships and hard-deletes join requests and EE grants (`TeamService.cs:481`);
  membership rows themselves are retained by legal basis.

## 5. Seams

- **`IRoleAssignmentService` / `ITeamResourceService`** carry the reconciler's
  read-only cross-section calls; mutations go through each owner's `I<Section>Write`.
- **Cross-camp / sub-team shift management** (guide: "the system is being updated to
  reflect this properly") — coordinator scoping for shifts is Shifts' call, shaped by
  `GetUserCoordinatedTeamIdsAsync`.
- **`google_resources`** is a Team Resources sub-aggregate owned and written by
  GoogleIntegration; its admin page is GoogleIntegration's (`/Google/Teams/{slug}/Resources`), which Teams only links to.

## 6. Deliberately not done

- **No `ITeamService` split into per-shape interfaces.** The service/write/management
  three-face split is the settled shape (`section-public-contract`); further carving
  buys nothing at one implementation.
- **No DB constraint for one-management-role-per-team or one-pending-request** — service
  guards plus tests (`db-enforcement-minimal`).
- **No per-team pending-request count in the cache** — coordinators need it live; the
  bypass is the design.
- **No DB-backed `SearchAsync`** — the inner service throws; search is cache-only by
  design, and the global search orchestrator ranks.
- **No merging `TeamResourceService` into Teams** — vendor connectors own their sections
  (`vendor-connectors-own-sections`, `team-resources-google-integration-section`).
- **No entity-returning members on the leaf** — controllers read `TeamInfo` via
  `GetTeamBySlugAsync` / `GetTeamAsync`; the internal entity reads are gone.
- **`EETeamAdmin` is not in `AnyAdminRole`** — its only surface is the per-team page.

## Load-bearing weirdness

- **`TeamService.InvalidateActiveTeamsCache` is an empty
  body.** The inner service has no cache; the decorator overrides it. Only Teams-internal callers
  hold the decorator, so the no-op is the correct inner behaviour, not a bug.
- **Keyed-Scoped inner + Singleton decorator.** The decorator opens a scope per call that
  reaches the inner service; `TeamService` is also registered unkeyed so its non-`ITeamService`
  roles (`IGoogleGroupMembershipSource`, `IUserDataContributor`, `IEarlyEntryProvider`) resolve
  to the same instance within a scope.
- **`HumansTeamControllerBase` is public under `Contracts/` in the section project, not on
  the leaf** — it derives from `HumansControllerBase` in Base's UI layer, which no leaf may
  reference; Shifts' admin controller derives from it.
- **Account merge evicts the team graph twice.** Teams' own `IUserMerge.ReassignAsync`
  evicts after its re-FK; Users' merge tail evicts again after the tombstone, because the
  graph stitches each member's `UserInfo` and system-team rows still name the archived id.
- **Pending-request lookups skip the cache** so a coordinator never sees a stale count.
- **The reconciler applies Inactive-before-Active-style deltas per team through the
  service** and fans out Google/audit calls itself; the service's bulk-apply members are
  its private protocol, not a general write API.
- **`ApplySystemTeamMembershipDeltaAsync` is also driven by `DevPersonaSeeder`** to put
  personas on the Board system team — the one non-job caller of the bulk-apply protocol.
- **Two admin surfaces for sync live under `/Google/*`**, not here: the job is Teams' own,
  the buttons are GoogleIntegration's.

## History

| Run | Date | Headline | PR |
|---|---|---|---|
| 1 | 2026-09-05 | First pass — leaf trimmed to its cross-section half, docs and comments re-derived from code, dead keys and duplicate tests cut, audit/approval/directory invariants pinned | peterdrier/Humans#1594 |
