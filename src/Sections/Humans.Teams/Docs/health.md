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

Every membership change is audit-logged, mirrored to the team's Google resources, and
reflected in the Coordinators system team when a management role is involved.

## 2. The shapes

| Question shape | Asked by | Answered by |
|---|---|---|
| "Which teams exist / what is team X?" (whole graph, by id, by slug, with parents, name fan-out, search) | almost every section, Shell tiles, global search, the auth handler | `ITeamServiceRead.GetTeamsAsync` / `GetTeamAsync` / `GetTeamBySlugAsync` / `GetTeamsWithParentsAsync` / `SearchAsync`; `IEntityNameContributor` |
| "What may this human do on this team?" (coordinator of, coordinated ids, budget-scoped ids) | Shifts, Expenses, GoogleIntegration, `TeamAuthorizationHandler` | `ITeamServiceRead.IsUserCoordinatorOfTeamAsync` / `GetUserCoordinatedTeamIdsAsync` / `GetEffectiveBudgetCoordinatorTeamIdsAsync`; `TeamCoordinatorAccess` |
| "Which teams is this human in?" | Users, GoogleIntegration, the profile card | `ITeamServiceRead.GetUserTeamMembershipsAsync` |
| "Show me the directory / a team page / my teams / the roster" | the section's own controllers | `ITeamManagementService.GetTeamDirectoryAsync` / `GetTeamDetailAsync` / `GetMyTeamMembershipsAsync` / `GetRosterAsync` / `GetAdminTeamListAsync`; `ITeamPageService` |
| "Let me in / let me out" (join, request, withdraw, leave; approve, reject) | members and coordinators via the controllers | `JoinTeamAsync` / `LeaveTeamAsync` / `WithdrawJoinRequestAsync` / `ApproveJoinRequestAsync` / `RejectJoinRequestAsync` + the two pending-request reads |
| "Shape the team" (create, edit, deactivate, page content, Google group prefix) | TeamsAdmin/Board/Admin via the controllers; GoogleIntegration for the prefix | `CreateTeamWithGoogleGroupAsync` / `UpdateTeamWithGoogleGroupAsync` / `DeleteTeamAsync` / `UpdateTeamPageContentAsync`; `ITeamService.SetGoogleGroupPrefixAsync` |
| "Staff the team" (members, role definitions, role assignments) | coordinators via the controllers | `AddMemberToTeamAsync` / `RemoveMemberAsync`; the role-definition members; `AssignToRoleAsync` / `UnassignFromRoleAsync` |
| "Who gets in early, for what?" | the per-team EE page; the EE roster | the EE-grant members; `IEarlyEntryProvider.GetEarlyEntriesAsync` |
| "Recompute the system teams" | Hangfire, the Google sync admin page, other sections' provisioning flows | `ISystemTeamSync` (job) over `ITeamService.ApplySystemTeamMembershipDeltaAsync` and the management interface's role-reconciliation members |
| "This human is gone / merged" | Users (deletion, merge), Gdpr | `RevokeAllMembershipsAsync` / `RemoveMemberFromAllTeamsCache`; `IUserMerge.ReassignAsync`; `IUserDataContributor` (export + erasure) |
| "Something changed underneath you" | Users, Development | `IActiveTeamsCacheInvalidator` → `InvalidateActiveTeamsCache` |
| "Seed a fixture" | Development, Budget dev seeders | `ITeamSeeding` |

Vocabulary the leaf carries: `TeamInfo` (+ `TeamMemberInfo`, `TeamRoleDefinitionSnapshot`,
`TeamRoleAssignmentSnapshot`), `UserTeamMembershipInfo`, `TeamSearchHit`, `SyncReport`,
`TeamMemberRole`, `RolePeriod`, `SlotPriority`, `TeamJoinRequestStatus`, `CallToAction` /
`CallToActionStyle`, `TeamCoordinatorAccess`.

## 3. Structure

The shapes imply:

- **A contracts leaf** with exactly the cross-section half: the read interface (the
  whole-graph, authority and membership shapes), a write interface carrying only members some *other* section
  calls, the system-sync contract, the seeding contract, the cache-invalidator contract,
  and the flat records those signatures name. A member with no caller outside the
  assembly does not belong on the leaf.
- **One inner service** owning every business rule, behind the internal management
  interface, talking only to `ITeamRepository` for its own tables and to other
  sections through their interfaces (audit, notifications, Google outbox, shifts auth
  eviction, early-entry eviction, user read model).
- **One singleton caching decorator** over the same interface holding the canonical
  `TeamInfo` graph, serving every read shape from memory, and clearing wholesale on every
  write so the next read re-warms the whole graph — no per-mutation diffing. Pending-request
  reads bypass it on purpose.
- **One repository** with thick, named methods; no LINQ leaks.
- **Controllers**: member-facing (`/Teams/*`) and per-team management
  (`/Teams/{slug}/*`), both thin, sharing one resolve-slug-then-authorise base. The
  resource-based handler plus its requirement live beside them.
- **One system-team reconciler** (the hourly job) that reads other sections' facts and
  applies deltas through the service, never the repository.
- **Pure helpers** for the directory grouping, the page summary, the EE projection —
  static, tested in isolation, no I/O.
- **The metrics gauge loop**, the section's seams (`Section*.cs`), views, one resx set.

## 4. Invariants

- At most one management role per team; only TeamsAdmin/Admin may set or clear the flag.
- Sub-team members count as department members for roster, legal requirements and Google
  access; a department coordinator has authority over its sub-teams; a sub-team manager
  has none over the department, its siblings, or Google resources.
- System-team membership is written only by the reconciler and its bulk-apply members;
  manual add/remove/join/leave on a system team is refused; role assignment on a system
  team never adds a non-member.
- Approval-required teams gain members only through an approved request; open teams add
  at once. One pending request per human per team; state history is append-only.
- Removing a member removes all their role assignments on that team; a management-role
  change reconciles the Coordinators system team for that human.
- Hidden teams are invisible to non-admins everywhere they would otherwise appear
  (directory, detail, join, birthdays, My Teams, search, profile cards of others).
- Only departments can have a public page; anonymous visitors see public departments
  only, coordinators-only member lists, no emails.
- `IsSensitive` is written only by a global Admin.
- Early-entry grants exist only on `EarlyEntryEnabled` teams; disabling keeps grants but
  hides them from the roster; every grant mutation is audited and evicts the user's EE cache.
- Every membership add/remove is audit-logged and mirrored to Google (add inline, remove
  via the nightly reconciliation); the outbox append commits with the team write.
- Slugs are unique across `Slug` and `CustomSlug`; both resolve.
- Erasure ends live memberships and hard-deletes join requests and EE grants; membership
  rows themselves are retained by legal basis.

## 5. Seams

- **`IRoleAssignmentServiceRead` / `ITeamResourceServiceRead`** do not exist yet; the
  reconciler's read-only cross-section calls stay on full interfaces until Auth and
  GoogleIntegration split (ledger 2026-08-14).
- **Cross-camp / sub-team shift management** (guide: "the system is being updated to
  reflect this properly") — coordinator scoping for shifts is Shifts' call, shaped by
  `GetUserCoordinatedTeamIdsAsync`.
- **`google_resources`** is a Team Resources sub-aggregate owned and written by
  GoogleIntegration; the Resources admin page here is a thin front over that section.

## 6. Deliberately not done

- **No `ITeamService` split into per-shape interfaces.** The read/write/management
  three-level split is the settled shape (`section-read-write-split`); further carving
  buys nothing at one implementation.
- **No DB constraint for one-management-role-per-team or one-pending-request** — service
  guards plus tests (`db-enforcement-minimal`).
- **No per-team pending-request count in the cache** — coordinators need it live; the
  bypass is the design.
- **No DB-backed `SearchAsync`** — the inner service throws; search is cache-only by
  design, and the global search orchestrator ranks.
- **No merging `TeamResourceService` into Teams** — vendor connectors own their sections
  (`vendor-connectors-own-sections`, `team-resources-google-integration-section`).
- **No entity-returning members on the leaf** — `GetTeamEntityBySlugAsync` /
  `GetTeamByIdAsync` are internal on purpose and shrink as the controllers move to `TeamInfo`.
- **`EETeamAdmin` is not in `AnyAdminRole`** — its only surface is the per-team page.

## Load-bearing weirdness

- **`TeamService.InvalidateActiveTeamsCache` / `RemoveMemberFromAllTeamsCache` are empty
  bodies.** The inner service has no cache; the decorator overrides them. Callers always
  hold the decorator, so the no-ops are the correct inner behaviour, not a bug.
- **Keyed-Scoped inner + Singleton decorator.** The decorator opens a scope per call that
  reaches the inner service; `TeamService` is also registered unkeyed so its non-`ITeamService`
  roles (`IGoogleGroupMembershipSource`, `IUserDataContributor`, `IEarlyEntryProvider`) resolve
  to the same instance within a scope.
- **`HumansTeamControllerBase` is public under `Contracts/` in the section project, not on
  the leaf** — it derives from `HumansControllerBase` in Base's UI layer, which no leaf may
  reference; Shifts' admin controller derives from it.
- **`IActiveTeamsCacheInvalidator` lives on the leaf but is grandfathered as an
  `IInvalidator`** — Users evicts through it after writes the service cannot see.
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
