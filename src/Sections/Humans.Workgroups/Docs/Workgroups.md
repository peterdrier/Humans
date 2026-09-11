<!-- freshness:triggers
  src/Sections/Humans.Workgroups/**
-->
<!-- freshness:flag-on-change
  Lifecycle transitions and the Dormant freeze, the one-or-two-coordinators rule, the
  document/comment-period rules, the daily rhythm job's actions, and the GDPR
  erase-attribution-keep-content posture — review when Workgroups services, entities,
  or controllers change.
-->

# Workgroups — Section Invariants

The register of association-level working groups the Board resolution obliges the
Secretary to keep. A register and a record, not a workflow engine: registration is
administrative recognition, and every decision is a human's.

## Concepts

- A **Workgroup** is one register entry: name, purpose, coordinators, target date,
  deliverable, audience, status, Drive folder, Discord link. Time-bound by design — a
  recurring subject is a new instance per year ("Finance 2027"), never a long-lived
  group. Does not wrap a Team; owns its own membership.
- The **deliverable sentence** is "By [TargetDate] we will deliver [Deliverable] to
  [Audience]."
- A **Coordinator** is one of the one or two people named on the register
  (`WorkgroupMemberRole.Coordinator`). Register-facing, not permission-facing in v1 —
  every member may do member work.
- The **Secretary** is a Board member; every Secretary action is `BoardOrAdmin`. No new role.
- A **Member** is any signed-in human who joined. Joining is immediate (standing approval).
- A **Meeting** is a dated session the group owns, optionally public, with minutes filled
  in afterwards.
- A **Log entry** is one dated line in the group's history. System kinds record lifecycle
  steps and never change; member kinds (Update, Disclosure, StatusRequested, Note) are
  written, edited and deleted by members.
- A **Document** is a Markdown artefact: Draft → Published → Delivered, an optional
  comment period, and, once Delivered, the Board's disposition.
- A **Comment** is a signed-in human's remark on a Published document during its open
  comment window, tagged with one of the document's categories, answered by the group.
- **Dormant** is the terminal status: delivered, abandoned, or closed for silence. Roster
  and documents stay readable; the Drive folder goes read-only. The Secretary may reactivate.

## Data Model

### Workgroup — `workgroups`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| Name | string(200) | |
| Slug | string(100) | Unique; admin-editable; re-slugs when Name changes |
| Purpose | string(4000) | Markdown, sanitized on render |
| Deliverable | string(500) | One line |
| DeliverableKind | WorkgroupDeliverableKind | |
| Audience | WorkgroupAudience | Board / Assembly / Community |
| TargetDate | LocalDate? | |
| Status | WorkgroupStatus | See Lifecycle |
| DormantReason | WorkgroupDormantReason? | Null unless Dormant |
| DriveFolderId | string(100)? | Null until Active |
| DiscordChannelUrl | string(500)? | |
| Reasons | string(4000)? | Refusal, withdrawal, or Quiet-close reasons |
| AppliedByUserId | Guid? | Bare cross-section reference; nulled on erasure |
| AppliedAt / RegisteredAt / EndedAt | Instant / Instant? / Instant? | |
| DormantSince | Instant? | Set by the job at 60 days' silence; cleared by the next Update/Meeting. Distinct from Dormant status — it is the inquiry flag |
| CreatedAt / UpdatedAt | Instant | |

Indexes: `Slug` unique; `Status`; `DriveFolderId` unique filtered non-null (one group per folder).

### WorkgroupMember — `workgroup_members`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| WorkgroupId | Guid | FK, Cascade |
| UserId | Guid | Bare cross-section reference; indexed, no FK |
| Role | WorkgroupMemberRole | Member / Coordinator |
| JoinedAt | Instant | |
| LeftAt | Instant? | Soft leave; roster shows current members, log keeps history |

Unique filtered `(WorkgroupId, UserId)` where `LeftAt IS NULL`.

### WorkgroupMeeting — `workgroup_meetings`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| WorkgroupId | Guid | FK, Cascade |
| Title | string(200) | |
| StartUtc / EndUtc | Instant | |
| Location / LocationUrl | string(500)? / string(2000)? | |
| IsPublic | bool | Public meetings feed the community calendar |
| Minutes | text? | Markdown, filled in afterwards |
| CreatedByUserId | Guid? | Bare reference; nulled on erasure |
| CreatedAt / UpdatedAt | Instant | |
| DeletedAt | Instant? | Soft delete; excluded everywhere outside the repository |

Index `(WorkgroupId, StartUtc)`.

### WorkgroupLogEntry — `workgroup_log_entries`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| WorkgroupId | Guid | FK, Cascade |
| Kind | WorkgroupLogKind | System or member kind — see Concepts |
| OccurredOn | LocalDate | Editable on member entries; system entries use the action date |
| Title | string(200)? | |
| Body | string(16000)? | Markdown, sanitized on render |
| AuthorUserId | Guid? | Bare reference; null for system entries and after erasure |
| DocumentId | Guid? | FK → WorkgroupDocument, SetNull |
| SurveyId | Guid? | Bare reference to a survey in Surveys |
| CreatedAt / UpdatedAt | Instant | |

Not §12 append-only by design: the log is a working record; the audit trail is the
immutable one. Member entries are editable and hard-deletable by any member (audited).
System entries never change.

`WorkgroupLogKind` — system: Applied, Registered, Referred, Refused, Withdrawn, Ended,
Reactivated, CoordinatorChanged, ScopeChanged, MemberJoined, MemberLeft,
DormancyInquiry, DocumentPublished, CommentPeriodOpened, CommentPeriodClosed, Delivered,
DispositionRecorded, SurveySubmitted, SurveySent. Member: Update, Disclosure,
StatusRequested, Note.

### WorkgroupDocument — `workgroup_documents`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| WorkgroupId | Guid | FK, Cascade |
| Title | string(200) | |
| Kind | WorkgroupDocumentKind | Deliverable / AnnualReport / Other |
| Body | text | Markdown, unbounded, sanitized on render |
| Status | WorkgroupDocumentStatus | Draft / Published / Delivered |
| CommentCategories | jsonb `List<string>` | Set before opening comments |
| CommentsOpenAt / CommentsCloseAt | Instant? | The window; open when now falls inside it |
| DeliveredAt | Instant? | |
| Disposition | WorkgroupDisposition? | Only ever set on Delivered |
| DispositionNote | string(4000)? | |
| DispositionAt / DispositionByUserId | Instant? / Guid? | Bare reference; nulled on erasure |
| CreatedByUserId / UpdatedByUserId | Guid? | Bare references; nulled on erasure |
| CreatedAt / UpdatedAt | Instant | |

Index `(WorkgroupId, Status)`.

### WorkgroupDocumentComment — `workgroup_document_comments`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| DocumentId | Guid | FK → WorkgroupDocument, Cascade |
| Category | string(100) | One of the document's categories as they stood at posting |
| AuthorUserId | Guid? | Bare reference; null after erasure — **body kept indefinitely** |
| Body | string(4000) | Markdown |
| CreatedAt | Instant | |
| Disposition | WorkgroupCommentDisposition | Pending / Accepted / Rejected / Incorporated / Noted |
| Response | string(4000)? | The group's answer; visible to all once set |
| RespondedByUserId / RespondedAt | Guid? / Instant? | Bare reference; nulled on erasure |
| HiddenAt / HiddenByUserId / HiddenReason | Instant? / Guid? / string(500)? | Moderation; hidden reads "hidden by the group" to non-admins, full text to admins |

Index `(DocumentId, Category)`.

### Enums

`WorkgroupStatus`: Applied, Referred, Active, Refused, Withdrawn, Dormant.
`WorkgroupDormantReason`: Delivered, Abandoned, Quiet.
`WorkgroupDeliverableKind`: Recommendation, DraftPolicy, DecisionBrief, Report,
AssemblyProposal, ResolutionProposal, DepartmentRegistration, Consultation, Event, Other.
`WorkgroupAudience`: Board, Assembly, Community.
`WorkgroupMemberRole`: Member, Coordinator.
`WorkgroupDocumentKind`: Deliverable, AnnualReport, Other.
`WorkgroupDocumentStatus`: Draft, Published, Delivered.
`WorkgroupDisposition`: Accepted, Declined, Deferred, Noted.
`WorkgroupCommentDisposition`: Pending, Accepted, Rejected, Incorporated, Noted.

### Settings

No section table. `SettingKeys.WorkgroupsRootDriveFolderId` (`Humans.Settings.Contracts`)
via `ISettingsService`, set at `/Workgroups/Admin/Settings`. Registration is refused with
a clear error while unset.

## Routing

| Route | Purpose |
|-------|---------|
| `/Workgroups` | Register: Active, Pending (Applied/Referred), Archive |
| `/Workgroups/Apply` | Clause 1 application form |
| `/Workgroups/{slug}` | Group page. Slug routes also resolve by the group's id, so a pre-rename link still lands |
| `/Workgroups/{slug}/Join`, `/Leave`, `/RequestStatus` | Member actions |
| `/Workgroups/{slug}/Edit`, `/Coordinators` | Register fields, coordinator handover |
| `/Workgroups/{slug}/Meetings/*`, `/Log/*` | Meetings and log entries |
| `/Workgroups/{slug}/Documents/*`, `/Comments/*` | Documents and comments |
| `/Workgroups/{slug}/Done` | Member ends the group: Dormant/Delivered or Dormant/Abandoned |
| `/Workgroups/{slug}/Surveys/Link` | Attach an authored survey |
| `/Workgroups/Admin/*` | Secretary/Board queue and decisions; `BoardOrAdmin`, localization-exempt |

See `authorization.md` for the auth policy per route.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Any signed-in human with an approved profile | Browse the register and every group page; read Published/Delivered documents, meetings and the log; join or leave a group; comment during an open window; request a status update; apply to form a group |
| Workgroup member | Additionally: read Draft documents; edit register fields; create/edit meetings and minutes; post Update, Disclosure and Note entries; create/edit/publish/deliver documents; open/close comment periods; respond to and dispose of comments; hide a comment with a reason; link an authored survey; mark the group done |
| Coordinator | Everything a member can. Named on the register; addressee of notifications; may hand coordination to another member. Register-facing distinction, not a separate permission level in v1 |
| Board, Admin (`BoardOrAdmin`) | Register, refer, refuse, withdraw, close, reactivate; set coordinators (override); register on behalf (bootstrapping); record a document's disposition; view the admin queue; edit any group; set the root Drive folder |

## Invariants

- Registration is administrative recognition only (Board Resolution clause 4). No
  transition happens automatically; every lifecycle step is a `BoardOrAdmin` action
  (`WorkgroupService.Lifecycle`).
- **One or two coordinators at all times on an Active group.** The last coordinator
  leaving must name a replacement (`LeaveAsync`); a `BoardOrAdmin` caller may override and
  leave the group coordinatorless on purpose so the Secretary can appoint someone.
  `SetCoordinatorsAsync` rejects a count outside 1–2 and any id that is not a current member.
- **A Dormant (or not-yet-Active) group freezes all member mutations.** `RequireAcceptsMemberWork`
  gates every member write on `Status == Active`; only `BoardOrAdmin` actions
  (register, refer, refuse, withdraw, close, reactivate, disposition, coordinator
  override) remain. The authorization handler denies the same operation client-side.
- Refused and Withdrawn require non-empty `Reasons`.
- Registration creates the Drive subfolder before the status flips; a folder-creation
  failure leaves the group Applied so the Secretary can retry.
- Reactivation reverses Dormant fully: status, Drive access (write again), and
  `DormantSince`/`Reasons` cleared.
- The 14-day application clock and the 60-day/74-day silence clocks are highlights and
  notifications only — see the daily rhythm below. Nothing auto-registers or auto-closes.
- Every lifecycle transition writes a system log entry and an `AuditLogEntry`
  (`relatedEntityId`/`Type` = the workgroup) via `AuditAsync`.
- A document's comment window may only be set on a Published document with at least one
  category, and must end before the document is Delivered (`OpenCommentsAsync`).
- Delivered freezes a document's body (`UpdateDocumentAsync` refuses further edits); a
  disposition may only be recorded on a Delivered document.
- Comments: the window is the only gate on `AddCommentAsync` — any signed-in human, group
  membership irrelevant, while `IsOpenForComment(now)` is true. A bulk category response
  (`RespondToCategoryAsync`) touches only still-Pending, non-hidden comments in that
  category — an individually-answered comment keeps its own answer.
- Comment/log/document responses stay possible after the comment window closes but not
  after the group leaves Active (`RequireAcceptsMemberWork` on the responding calls too).
- A group's Slug never collides with a reserved route segment (`apply`, `admin`).

## Negative Access Rules

- A non-member **cannot** read a Draft document, post a log entry, create a meeting,
  edit register fields, or reach any document-mutation route.
- A member **cannot** register, refer, refuse, withdraw, close, reactivate, or record a
  document's disposition — those are `BoardOrAdmin` only.
- Nobody **cannot** comment outside an open comment window, or on a Draft document.
- A Dormant group's members **cannot** perform any member mutation (log, meetings,
  documents, comments, register edits) — only `BoardOrAdmin` actions remain.
- Anonymous requests **cannot** reach any `/Workgroups*` route — `PolicyNames.AppAccess`
  gates the member controller class-wide, `BoardOrAdmin` gates the admin controller.
- A member **cannot** leave as the last coordinator without naming a replacement
  (`BoardOrAdmin` may override).

## Triggers

- Apply: the applicant becomes the first coordinator (plus an optional second); writes
  `Applied`; notifies and emails the Board.
- Register: creates the Drive subfolder, flips to Active, writes `Registered`, audits,
  notifies/emails coordinators, requests a Drive sync.
- Refer/Refuse/Withdraw/Close/Reactivate: system log entry, audit entry, coordinators
  notified/emailed; Withdraw and Reactivate also request a Drive sync (write access changes).
- Join/Leave: system log entry (`MemberJoined`/`MemberLeft`), Drive sync requested; a
  forced coordinator handover on last-coordinator leave also writes `CoordinatorChanged`.
- An Update log entry or a new/edited meeting clears `DormantSince` — the only two
  activity kinds §13 counts as a sign of life.
- Publish/OpenComments/CloseComments/Deliver: system log entry; Publish and OpenComments
  notify current members; Deliver notifies and emails the Board.
- RecordDisposition: system log entry, audit entry, members notified, coordinators emailed.
- HideComment: audited, so the Board can review moderation even though the row shows
  "hidden by the group" to everyone else.
- Erasure of a group's only coordinator on an Active group notifies the Board role that
  the group has no coordinator (`WorkgroupService.Gdpr`).
- The daily rhythm job (below) — every action it takes writes a log entry, an audit entry
  (attributed to the job, not a human), and a notification.

## Daily Rhythm (design §13)

One Hangfire job (`workgroups-rhythm`, 06:00 daily), calling
`IWorkgroupService.RunDailyRhythmAsync`. **It never registers, closes, or refuses
anything — every action here is a notice, a flag, or a record; a human still has to act.**

| Condition | Action |
|-----------|--------|
| Active, no Update/meeting in 30 days | Notify coordinators, in-app, once per 30-day window |
| Active, no Update/meeting in 60 days, `DormantSince` null | Set `DormantSince`, write `DormancyInquiry`, notify+email coordinators, notify Board |
| `DormantSince` ≥ 14 days old, still silent | Notify Board "close candidate" |
| Applied/Referred, ≥ 14 days old | Notify Board once |

Status-overdue and disposition-overdue are read-time badges computed by `WorkgroupRhythm`
against the caller's clock, not job actions — a cached register can never show a stale
badge. "Request a status update" is a member action (any signed-in human, once per group
per 7 days), not part of the job.

## Cross-Section Dependencies

- **Users**: `IUserServiceRead` — names, `MembershipTier`, approved-profile check.
- **Teams**: `ITeamServiceRead` — `SystemTeamIds.Board` membership for badges and root Drive readers.
- **Calendar**: implements `ICalendarFeedContributor` (inbound) — per-user meetings of
  Active groups the user belongs to; public meetings of Active groups for the community
  calendar window. Calendar names nothing of Workgroups.
- **GoogleIntegration**: implements `IGoogleDriveAccessSource` (inbound) — Active group
  folders → Contributor for current members, Dormant → Viewer, root → Board/Asociado/Colaborador
  readers. Calls `IGoogleSyncService.CreateSubfolderAsync` (registration) and
  `RequestSyncAsync` (every access-relevant change) outbound.
- **Settings**: `ISettingsService` — the root Drive folder id.
- **Surveys**: none directly — a group links a survey it authored by id; Surveys never references Workgroups.
- **Notifications, Email, AuditLog**: crosscuts, per Triggers above.
- **Gdpr**: `IUserDataContributor`, `IUserMerge` — see GDPR below.

## GDPR

- **Export** (`ContributeForUserAsync`): five slices — memberships (role, dates), log
  entries, meetings created, documents created/updated, comments (including hidden ones,
  with disposition and response).
- **Erasure** (`EraseForUserAsync`): nulls attribution everywhere (`AuthorUserId`,
  `CreatedByUserId`, `UpdatedByUserId`, `RespondedByUserId`, `AppliedByUserId`,
  `HiddenByUserId`, `DispositionByUserId`). **Content stays** — comments, log bodies,
  minutes and documents are the association's record and remain indefinitely (Art.
  17(3)(b)). Membership rows are deleted. If the erased person was the last coordinator
  of an Active group, the Board is notified.
- **Merge** (`ReassignAsync`/`IUserMerge`): folds every user-id column from source to
  target, collapsing duplicate membership rows the fold creates.
- **Consent**: nothing new is gated — joining is the member's own action.

## Architecture

**Owning services:** `WorkgroupService` (inner, keyed `"workgroups-inner"`),
`CachingWorkgroupService` (Singleton decorator)
**Owned tables:** `workgroups`, `workgroup_members`, `workgroup_meetings`,
`workgroup_log_entries`, `workgroup_documents`, `workgroup_document_comments`
**Status:** (A) Migrated — new section, built in this shape from day one.

### Cross-section read interface

| Read interface | Methods | Notes |
|---|---:|---|
| `IWorkgroupServiceRead` | — | Not published — no other section consumes Workgroups directly (Calendar and GoogleIntegration are inbound fan-outs Workgroups implements); no `.Contracts` leaf |

### For (A) Migrated sections

- `WorkgroupService` lives in `src/Sections/Humans.Workgroups/Services/` and never
  imports `Microsoft.EntityFrameworkCore`.
- `IWorkgroupRepository` (impl `WorkgroupRepository`,
  `IDbContextFactory<WorkgroupsDbContext>`) is the only code path that touches this
  section's tables. Reads no-tracking; the whole register loads as one graph
  (`WorkgroupsGraph`) and the service works over it in memory — a few dozen rows.
- **Decorator decision** — caching decorator (`CachingWorkgroupService`, Singleton). One
  `TrackedCache<byte, IReadOnlyList<WorkgroupInfo>>` (`Workgroups.Register`), single
  entry for the whole register, cleared on every write. Nothing time-derived is cached —
  `WorkgroupRhythm` computes rhythm badges from the snapshot against the caller's clock.
- **Display stitching** — `IUserServiceRead.GetUserInfosAsync` for burner names and tiers.
- **Cross-section calls** — `IUserServiceRead`, `IUserEmailService`,
  `IRoleAssignmentService`, `ITeamServiceRead`, `ISettingsService`, `IGoogleSyncService`,
  `INotificationService`, `IEmailService`, `IEmailMessageFactory`, `IAuditLogService`, `IClock`.
- **Architecture test** — none yet; `tests/Humans.Workgroups.Tests` does not exist on
  disk as of this doc. Add `Architecture/WorkgroupsArchitectureTests.cs` per the
  pattern in other (A) sections when the test project is created.
