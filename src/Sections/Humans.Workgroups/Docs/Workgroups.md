<!-- freshness:triggers
  src/Sections/Humans.Workgroups/**
  tests/Humans.Workgroups.Tests/**
  src/Sections/Humans.Users.Contracts/IUserService.cs
  src/Sections/Humans.Users.Contracts/IUserEmailService.cs
  src/Sections/Humans.Auth/Contracts/IRoleAssignmentService.cs
  src/Sections/Humans.Teams.Contracts/ITeamService.cs
  src/Sections/Humans.Finance.Contracts/IHoldedFinanceWrite.cs
  src/Sections/Humans.Holded/Contracts/IHoldedService.cs
  src/Sections/Humans.GoogleIntegration/Contracts/IGoogleSyncService.cs
  src/Sections/Humans.GoogleIntegration/Contracts/IGoogleDriveAccessSource.cs
  src/Sections/Humans.Calendar/Contracts/ICalendarFeedContributor.cs
  src/Sections/Humans.Surveys/Contracts/ISurveyAnalysisRead.cs
  src/Sections/Humans.Settings/Contracts/ISettingsService.cs
-->
<!-- freshness:flag-on-change
  Lifecycle transitions and the Dormant freeze, the one-or-two-coordinators rule, the
  document/comment-period rules, the daily rhythm job's actions, the budget rules
  (amount and Holded account ownership, clearing keeps the binding), and the GDPR
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
  (`WorkgroupMemberRole.Coordinator`). Every member may do member work; the one thing a
  coordinator holds that a member does not is the membership decision — who joins.
- The **Secretary** is a Board member; every Secretary action is `BoardOrAdmin`. No new role.
- Selected members and coordinators must resolve to existing non-terminal accounts before admission, application or existing-group registration writes. Merge aliases use the survivor; a second-coordinator alias of the applicant creates no duplicate membership. Registration validates people before budget or Drive creation. This adds no admission-state restriction for live accounts.
- A **Member** is any signed-in human a coordinator (or Board/Admin) let in: a join is a
  **request** (`IsPending`) until approved, or the coordinator adds the person directly.
- A **Sensitive** group keeps its roster, documents, meetings and history to its members and
  Board/Admin. Name, status, purpose, the deliverable sentence and the coordinators stay on
  the register: the coordinators are the group's public contact (the people the resolution
  names), and asking to join needs someone to ask.
- A **Meeting** is a dated session the group owns, optionally listed in the community
  calendar, with minutes filled in afterwards. Everyone with app access can read
  meetings and minutes on the group page; calendar listing does not change that access.
- A **Log entry** is one dated line in the group's history. System kinds record lifecycle
  steps and never change; member kinds (Update, Disclosure, StatusRequested, Note) are
  written, edited and deleted by members.
- A **Document** is a Markdown artefact: Draft → Published → Delivered, an optional
  comment period, and, once Delivered, the Board's disposition.
- A **Comment** is a signed-in human's remark on a Published document during its open
  comment window, tagged with one of the document's categories, answered by the group.
- **Dormant** is the terminal status: delivered, abandoned, or closed for silence. Roster
  and documents stay readable; the Drive folder goes read-only. The Secretary may reactivate.
- A **Budget** is an optional EUR allocation on the register plus the Holded expense
  account the group's spending books to. Finance owns the account's creation and naming
  ("Workgroups / {Name}"); Workgroups owns the amount and the reference. Visible to
  Board/Admin, Colaborador and Asociado.

## Data Model

### Workgroup — `workgroups`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| Name | string(200) | |
| Slug | string(100) | Admin-editable; re-slugs when Name changes. Nonblank names without ASCII slug characters use the `workgroup` seed and the usual collision suffixing. Plain index, not unique — uniqueness is the service's job (`ReserveSlugAsync`), never a DB constraint on an editable string |
| Purpose | string(4000) | Markdown, sanitized on render |
| Deliverable | string(500) | One line |
| DeliverableKind | WorkgroupDeliverableKind | |
| Audience | WorkgroupAudience | Board / Assembly / Community |
| TargetDate | LocalDate? | |
| Status | WorkgroupStatus | See Lifecycle |
| DormantReason | WorkgroupDormantReason? | Null unless Dormant |
| DriveFolderId | string(100)? | Null until Active |
| DiscordChannelUrl | string(500)? | Optional absolute HTTP/HTTPS link. Applications, registration on behalf and edits reject other schemes before writes; projections omit unsafe legacy values without changing stored data. |
| IsSensitive | bool? | Roster, documents, meetings and history for members and Board/Admin only; null reads as false (nullable so the column carries no physical default) |
| Reasons | string(4000)? | Refusal, withdrawal, or Quiet-close reasons |
| AppliedByUserId | Guid? | Bare cross-section reference; nulled on erasure |
| AppliedAt / RegisteredAt / EndedAt | Instant / Instant? / Instant? | |
| CreatedAt / UpdatedAt | Instant | |
| BudgetAmount | decimal(18,2)? | Null = no budget |
| HoldedAccountNumber | int? | Bare external reference, no FK; kept when the budget is cleared |
| HoldedAccountId | string(64)? | |

Indexes: `Slug`; `Status`; `DriveFolderId` unique filtered non-null (one group per folder — a Google-assigned opaque id, not editable display data).

### WorkgroupMember — `workgroup_members`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| WorkgroupId | Guid | FK, Cascade |
| UserId | Guid | Bare cross-section reference; indexed, no FK |
| Role | WorkgroupMemberRole | Member / Coordinator |
| JoinedAt | Instant | Request time until approval, then the approval time |
| LeftAt | Instant? | Soft leave; roster shows current members, log keeps history. A declined or withdrawn request is a pending row with `LeftAt` set |
| IsPending | bool? | True from the join request until a coordinator or Board/Admin approves it. A pending row is not a member anywhere: no member work, no Drive access, no roster slot, not a coordinator candidate. Null reads as false (nullable so the column carries no physical default) |

Unique filtered `(WorkgroupId, UserId)` where `LeftAt IS NULL`.

### WorkgroupMeeting — `workgroup_meetings`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| WorkgroupId | Guid | FK, Cascade |
| Title | string(200) | |
| StartUtc / EndUtc | Instant | |
| Location / LocationUrl | string(500)? / string(2000)? | |
| IsPublic | bool | Adds the meeting to the community calendar; register visibility stays AppAccess |
| Minutes | text? | Markdown, filled in afterwards |
| CreatedByUserId | Guid? | Bare reference; nulled on erasure |
| CreatedAt / UpdatedAt | Instant | |
| DeletedAt | Instant? | Soft delete; excluded everywhere outside the repository |

Indexes `(WorkgroupId, StartUtc)` and `CreatedByUserId` filtered non-null — the erasure
predicate reads that column.

Meeting URLs accept up to 2000 characters. The service validates the limit for creates
and edits; form failures preserve the entered values and show a localized error.

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

Indexes `(WorkgroupId, OccurredOn)` and `AuthorUserId` filtered non-null.

Not §12 append-only by design: the log is a working record; the audit trail is the
immutable one. Member entries are editable and hard-deletable by any member (audited).
System entries never change.

Member log bodies are limited to 16000 characters and lifecycle reasons to 4000.
Forms advertise these bounds and the service rejects overflow before writing any state.
Validation failures re-render the submitted form; admin lifecycle decisions (refuse, refer,
withdraw, close, reactivate, coordinator override) are made from the group's Details page
(Board/Admin actions panel) and redirect back to it, flashing any rule failure. Register
alone stays on the queue. Register-existing requires an explicit valid registration date before creating a group, Drive folder, budget account, audit or notice; an omitted date never becomes the type's default backdate. Explicit backdated dates remain supported.

`WorkgroupLogKind` — system: Applied, Registered, Referred, Refused, Withdrawn, Ended,
Reactivated, CoordinatorChanged, ScopeChanged, MemberJoined, MemberLeft,
DormancyInquiry, DocumentPublished, CommentPeriodOpened, CommentPeriodClosed, Delivered,
DispositionRecorded, SurveySubmitted, SurveySent, BudgetSet. Member: Update, Disclosure,
StatusRequested, Note.

`DormancyInquiry` is no longer written by anything. The member stays because `Kind` is
stored as a string and a group bootstrapped with a backdated `RegisteredAt` may already
carry rows the old daily job wrote; dropping it would break reading those.

**Linking a survey writes a trusted record, so the id is checked first.** `LinkSurveyAsync`
reads the posted survey through `ISurveyAnalysisRead.GetSummariesAsync` and rejects
(`Workgroups_Error_SurveyNotYours`) unless it exists and the actor authored it — otherwise the
log could name somebody else's survey, or none at all.

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

Indexes `(WorkgroupId, Status)` and `CreatedByUserId` filtered non-null.

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

Indexes `(DocumentId, Category)` and `AuthorUserId` filtered non-null.

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

`workgroups_settings` owns the root Drive folder id: one row (`Id = 1`),
`RootDriveFolderId` required, max 1000 characters. The existing admin form saves it
through the Workgroups repository and audits the actor. A save is refused unless
the service account can read the folder (`IGoogleSyncService.IsFolderReachableAsync`),
so a stale id surfaces on save, not at the next registration. Until that first save,
reads fall back to the legacy Settings key; reads never backfill or mutate it.
New section state is never written to the shared store. Registration is refused with
a clear error while unset.

## Routing

| Route | Purpose |
|-------|---------|
| `/Workgroups` | Register: Active, Pending (Applied/Referred), Archive |
| `/Workgroups/Apply` | Clause 1 application form |
| `/Workgroups/{slug}` | Group page. Slug routes also resolve by the group's id, so a pre-rename link still lands |
| `/Workgroups/{slug}/Join`, `/Leave`, `/RequestStatus` | Member actions. Join is a request; Leave while pending withdraws it |
| `/Workgroups/{slug}/Members/{userId}/Approve`, `/Decline`, `/Members/Add` | Membership decisions: a coordinator of an Active group, or Board/Admin |
| `/Workgroups/{slug}/Edit`, `/Coordinators` | Register fields, coordinator handover |
| `/Workgroups/{slug}/Meetings/*`, `/Log/*` | Meetings and log entries |
| `/Workgroups/{slug}/Documents/*`, `/Comments/*` | Documents and comments |
| `/Workgroups/{slug}/Done` | Member ends the group: Dormant/Delivered or Dormant/Abandoned |
| `/Workgroups/{slug}/Surveys/Link` | Attach an authored survey |
| `/Workgroups/Admin/*` | Secretary/Board overview queue and decision POSTs; `BoardOrAdmin`, localization-exempt. The queue is read-only except Register and Record disposition; the other decisions are forms on the group's Details page (the hardcoded-English Board/Admin panel, like Budget) that post here with the group's slug |
| `/Workgroups/Admin/{id}/Budget` | Set, change or clear the budget; `BoardOrAdmin` |

See `authorization.md` for the auth policy per route.

The Workgroups admin sidebar link stays visible with an empty queue so Board/Admin can reach
Settings and Register an existing group on first setup or after clearing the queue.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Any signed-in human with an approved profile | Browse the register and every group page; read Published/Delivered documents, meetings and the log of a non-sensitive group; ask to join, withdraw the request, or leave; comment during an open window (non-sensitive groups); request a status update; apply to form a group |
| Workgroup member | Additionally: read Draft documents; everything on a sensitive group's page; edit register fields (including the sensitive flag); create/edit meetings and minutes; post Update, Disclosure and Note entries; create/edit/publish/deliver documents; open/close comment periods; respond to and dispose of comments; hide a comment with a reason; link an authored survey; mark the group done |
| Coordinator | Everything a member can, plus the membership decision: approve or decline join requests and add people directly. Named on the register; addressee of notifications and join requests; may hand coordination to another member |
| Board, Admin (`BoardOrAdmin`) | Register, refer, refuse, withdraw, close, reactivate; set coordinators (override); approve, decline and add members on any group; register on behalf (bootstrapping); record a document's disposition; view the admin queue; edit any group; read sensitive groups; set the root Drive folder; set the budget and its Holded account |

## Invariants

- Roster membership-tier badges use localized enum labels in all six cultures.

- The section’s things-to-do contribution forwards caller cancellation through all data reads. Aborted reads propagate instead of being logged or converted into missing prompts.

- Coordinator-picker prefills pair the resolved human’s name and ID, including when the form holds an archived account ID.

- Opt-outable email builders capture the recipient/template culture on `EmailMessage` before restoring their rendering scope; Email renders the shared unsubscribe footer in that culture.

- The visible workgroup budget card uses the shared euro formatter in the selected UI culture.

- Budget preparation remains cancellable. Account creation or linking runs independently of browser cancellation through Finance, then persists the binding, system log and audit. Amount-only saves retain cancellation until their save succeeds, then finish the log and audit independently.

- Joining, leaving and changing coordinators keep preparation and the membership save cancellable. Once membership commits, system logs, handover/coordinator audits, notices and Drive sync requests finish independently of browser cancellation. Document publishing, comment-window changes, delivery and comment replies follow the same boundary: their save remains cancellable, then system logs and notices finish independently. Referral, refusal, withdrawal, reactivation, member/Secretary endings and Board disposition use this boundary too, including their audits and account activation changes. Register edits finish their scope-change log after saving; recorded status requests finish their coordinator notice.

- Member and admin action and form refusals preserve typed drafts and remain visible at Warning without exception stacks; action and resource-key context, redirects and localized form feedback are preserved. Only typed section refusals carrying untranslated resource keys and arguments become validation feedback; unexpected dependency lookup or access failures propagate to the application error handler instead of becoming workgroup 404/403 responses.
- Redirecting member actions reject malformed bound input after authorization and resource ownership checks, before any write. Invalid coordinator selections cannot save a valid subset, and invalid completion reasons cannot default to delivered; refusals return 400 and log a warning. Completion reasons, individual/category comment-response dispositions and Board dispositions must be posted explicitly; omitted choices cannot become delivered, pending or accepted. Explicit zero-valued choices remain valid. Admin binding refusals also log a Warning after actor resolution.

- Member workgroup pages and forms localize breadcrumb navigation labels in all six supported cultures.

- Application/register, meeting, log entry and document form required and length errors use shared resources in all six cultures; input limits are unchanged.

- Registration is administrative recognition only (Board Resolution clause 4). No
  transition happens automatically; every lifecycle step is a `BoardOrAdmin` action
  (`WorkgroupService.Lifecycle`).
- **Joining is a request; a coordinator decides.** `JoinAsync` writes a pending row and
  notifies the coordinators; nothing else changes. `ApproveMemberAsync` and `AddMemberAsync`
  are the only ways a person becomes a member (`AdmitAsync`): both need a current coordinator
  or `asAdmin`, and both write `MemberJoined`, audit, notify the person and request a Drive
  sync. `DeclineMemberAsync` sets `LeftAt` on the pending row, audits and notifies without a
  reason. A plain member gets `NotACoordinator`. Neither `SetCoordinatorsAsync` nor a
  last-coordinator handover in `LeaveAsync` ever promotes a pending row. Ending or
  withdrawing the group closes its pending requests (`ClosePendingRequestsAsync`), so
  nothing waits on a group nobody can decide for.
- **A sensitive group's content is its members'.** `IsSensitive` hides roster, documents,
  meetings and history from everyone but members and Board/Admin: the page shows the
  register fields and the join button, the Document route answers 404, and
  `AddCommentAsync` refuses a non-member (`MembersOnly`). The community calendar skips its
  public meetings (the members' own feed keeps them) and the Governance card shows its
  register entry without next meeting, comment or update-due badge; the register and the page header
  show its open-for-comment and update-due badges to members only (Board/Admin on the
  page). The register listing itself, the join request and the status request are unchanged.
- **One or two coordinators at all times on an Active group.** The last coordinator
  leaving must name a replacement (`LeaveAsync`); a `BoardOrAdmin` caller may override and
  leave the group coordinatorless on purpose so the Secretary can appoint someone.
  `SetCoordinatorsAsync` rejects a count outside 1–2 and any id that is not a current member.
- **A Dormant (or not-yet-Active) group freezes all member mutations.** `RequireAcceptsMemberWork`
  gates every member write on `Status == Active`; only `BoardOrAdmin` actions
  (register, refer, refuse, withdraw, close, reactivate, disposition, coordinator
  override) remain. Controllers and page controls use the resource authorization handler.
- Refused and Withdrawn require non-empty `Reasons`.
- Admin actions through the shared decision handler reject binding errors after actor resolution and before mutation. A malformed disposition cannot default to Accepted; no system log, audit or notification is emitted for a rejected form.
- Registration creates the Drive subfolder before the status flips; a folder-creation
  failure leaves the group Applied so the Secretary can retry. Application and both
  registration paths run to completion independently of request cancellation. Both are marked `[ExternalWrite]`, and their admin write calls explicitly pass `CancellationToken.None`; authorization and form reads retain the request token.
- Reactivation reverses Dormant fully: status, Drive access (write again), and `Reasons`
  cleared.
- The 14-day application clock and the 30-day update clock are notices only — see the daily
  rhythm below. Nothing auto-registers or auto-closes, and nothing tracks whether a group
  has gone quiet: ending a group is the Board's decision on its own reading of the register.
- Every lifecycle transition writes a system log entry and an `AuditLogEntry`
  (`relatedEntityId`/`Type` = the workgroup) via `AuditAsync`. Membership decisions
  (approve, decline, add) carry the subject as `relatedEntityId`/`Type = User`, so the
  person's GDPR export finds the rows that name them.
- A document's comment window may only be set on a Published document with at least one
  category, and must end before the document is Delivered (`OpenCommentsAsync`). Each
  category has to fit the 100 characters a comment's own `Category` column holds, or the
  window would accept comments that could never be saved.
- Delivered freezes a document's body (`UpdateDocumentAsync` refuses further edits); a
  disposition may only be recorded on a Delivered document. Deferred remains in the
  awaiting-disposition queue until a final reply is recorded.
- Ending the group is gated the same way: `MarkDoneAsync` refuses while any of the group's
  documents still has a comment window open.
- A comment window must end before delivery: `DeliverDocumentAsync` refuses while
  `CommentsCloseAt` is still in the future. Closing the window early is one click and keeps
  the comments, so the promised period is never cut short by a delivery.
- Document disposition notes, comment bodies and optional responses are limited to 4000
  characters after trimming; hiding reasons to 500. Services return the localized
  length refusal before changing the document or comments, saving or sending notices.
- Comments: the window is the only gate on `AddCommentAsync` — any signed-in human, group
  membership irrelevant, while `IsOpenForComment(now)` is true. A bulk category response
  (`RespondToCategoryAsync`) touches only still-Pending, non-hidden comments in that
  category — an individually-answered comment keeps its own answer.
- Comment/log/document responses stay possible after the comment window closes but not
  after the group leaves Active (`RequireAcceptsMemberWork` on the responding calls too).
- A group's Slug never collides with a reserved route segment (`apply`, `admin`).
- Budget: amount ≥ 0 or null and must be whole cents (at most two decimal places; trailing zeroes are valid). Updates and existing-group registration refuse fractional cents before Finance, Drive creation or persistence, using literal feedback on the Board/Admin-only forms. Refused/Withdrawn rejected; Finance is called before
  anything is written, so a failed create or an unknown account number changes nothing;
  the first account bound is created as "Workgroups / {Name}" or linked to an existing
  account of that normalized name; clearing keeps the binding; rebinding never touches
  the old account. Registering an existing group with a budget resolves the account before
  anything is written, so a Holded failure registers nothing.

## Negative Access Rules

- A non-member **cannot** read a Draft document, post a log entry, create a meeting,
  edit register fields, or reach any document-mutation route.
- A member **cannot** register, refer, refuse, withdraw, close, reactivate, or record a
  document's disposition — those are `BoardOrAdmin` only.
- A plain member **cannot** approve, decline or add members — a coordinator or `BoardOrAdmin` can.
- A pending requester **cannot** do member work, see a sensitive group's content, hold Drive
  access or be named coordinator; they can only withdraw the request.
- A non-member **cannot** read a sensitive group's roster, documents, meetings or history,
  nor comment on its documents; Board/Admin can.
- Nobody can comment outside an open comment window, or on a Draft document.
- A Dormant group's members **cannot** perform any member mutation (log, meetings,
  documents, comments, register edits) — only `BoardOrAdmin` actions remain.
- Anonymous requests **cannot** reach any `/Workgroups*` route — `PolicyNames.AppAccess`
  gates the member controller class-wide, `BoardOrAdmin` gates the admin controller.
- A member **cannot** leave as the last coordinator without naming a replacement
  (`BoardOrAdmin` may override).
- A Volunteer **cannot** see a group's budget: neither the budget card nor the `BudgetSet`
  History entries render unless the viewer is `BoardOrAdmin`, Colaborador or Asociado.

## Triggers

Workgroups sends full member and Board notice copy to Notifications, which fits previews to storage: 200 Unicode characters for titles,
2,000 for bodies. Oversized titles move into the body before the detail excerpt; ellipses mark
shortened copy. Full names and notes remain in the register and system log.
Notification recipient lookup and delivery failures are logged without failing committed
changes or skipping their email, Drive sync or Finance update. Recipient-language groups
remain independent. The rhythm nudge audit records successful dispatch requests, not inbox delivery (which
respects notification preferences); if every dispatch fails, no success audit is written. GDPR orphan notices use the same Board dispatch path.
Caller cancellation still propagates.

- Apply: the applicant becomes the first coordinator (plus an optional second); writes
  `Applied`; notifies and emails the Board.
- Register: creates the Drive subfolder, flips to Active, writes `Registered`, audits,
  notifies/emails coordinators, requests a Drive sync.
- Refer/Refuse/Withdraw/Close/Reactivate: system log entry, audit entry, coordinators
  notified/emailed; Withdraw and Reactivate also request a Drive sync (write access changes).
- Join: a pending row; coordinators notified (`WorkgroupMembershipRequested`, naming nobody —
  the pending row is the only copy of who asked, and erasure deletes it); no log, no sync.
  Approve/Add: `MemberJoined`, audit (`WorkgroupMembershipApproved` / `WorkgroupMemberAdded`),
  the person notified (`WorkgroupMembershipDecided`), Drive sync requested. Decline: audit
  (`WorkgroupMembershipDeclined`), the person notified. Leave: `MemberLeft`, Drive sync; a
  forced coordinator handover on last-coordinator leave also writes `CoordinatorChanged`.
  Withdrawing a pending request writes nothing.
  Names needed for membership/coordinator logs are read before persistence; a failed lookup
  leaves membership unchanged. Handover reuses the promoted person’s name for log and audit.
- Publish/OpenComments/CloseComments/Deliver: system log entry; Publish and OpenComments
  notify current members; Deliver notifies and emails the Board.
- RecordDisposition: system log entry, audit entry, members notified, coordinators emailed.
- HideComment: audited, so the Board can review moderation even though the row shows
  "hidden by the group" to everyone else.
- Erasure of a group's only coordinator on an Active group notifies the Board role that
  the group has no coordinator (`WorkgroupService.Gdpr`).
- The daily rhythm job (below) — every action it takes writes an audit entry (attributed to
  the job, not a human) and a notification; it writes no log entries.
- SetBudget: system log entry (`BudgetSet`, attributed to the actor), audit entry.
  Close/Done/Withdraw: retire the account in Finance's registry; Reactivate restores it;
  a Finance failure there is logged, never blocks the transition.

## Daily Rhythm (design §13)

One Hangfire job (`workgroups-rhythm`, 06:00 daily), calling
`IWorkgroupService.RunDailyRhythmAsync`. **It never registers, closes, or refuses
anything — every action here is a notice, a flag, or a record; a human still has to act.**

| Condition | Action |
|-----------|--------|
| Active, no Update/meeting in 30 days | Notify coordinators, in-app, once per 30-day window |
| Applied/Referred, ≥ 14 days old | Notify Board once |

Disposition-overdue is a read-time badge computed by `WorkgroupRhythm` against the caller's
clock, not a job action — a cached register can never show a stale badge. "Request a status
update" is a member action (any signed-in human, as often as they like), not part of the job.

The job reads how long a group has been quiet — that is how the 30-day nudge knows whether
its window has come round again — but it never persists a judgement about it: no flag, no
column, no log entry, and no move to `Dormant` or any other status. Only a human sets a
group Dormant. A group that stops reporting keeps getting the nudge; whether that group
should end is the Board's decision, taken on the register in front of them.

## Cross-Section Dependencies

- **Audit actions:** declared on `WorkgroupsAuditActions` (`src/Sections/Humans.Workgroups/WorkgroupsAuditActions.cs`), one `AuditActionDefinition` each, written through `IAuditLogService`: `WorkgroupRegistered`, `WorkgroupReferred`, `WorkgroupRefused`, `WorkgroupWithdrawn`, `WorkgroupClosed`, `WorkgroupReactivated`, `WorkgroupCoordinatorsChanged`, `WorkgroupDispositionRecorded`, `WorkgroupRegisteredExisting`, `WorkgroupBudgetSet`, `WorkgroupApplicationOverdue`, `WorkgroupUpdateDueNotified`, `WorkgroupLogEntryDeleted`, `WorkgroupLogEntryUpdated`, `WorkgroupMeetingUpdated`, `WorkgroupMeetingDeleted`, `WorkgroupCommentHidden`, `WorkgroupsRootFolderUpdated`, `WorkgroupMembershipApproved`, `WorkgroupMembershipDeclined`, `WorkgroupMemberAdded`.
- **Notifications:** `INotificationEmitter` (and `INotificationAutoResolve` where a source resolves). Its sources are declared in `WorkgroupsNotificationSources` (`src/Sections/Humans.Workgroups/WorkgroupsNotificationSources.cs`), one `NotificationSourceDefinition` each: `WorkgroupRegistrationPending` (actionable, to board role: a working group applies, or its application goes overdue); `WorkgroupRegistrationDecided` (informational, to the group's coordinators: registration decided (registered, refused, withdrawn, ended, reactivated)); `WorkgroupReportingDue` (informational, to the group's coordinators: thirty days without an update or meeting); `WorkgroupDocumentActivity` (informational (actionable to board when a document is delivered for disposition), to group members, comment authors, or Board role: document published, comment opened/answered, or delivered for disposition); `WorkgroupDispositionRecorded` (informational, to the group's current members: board records its written disposition on a delivered document); `WorkgroupMembershipRequested` (informational, to the group's coordinators: someone asks to join a working group); `WorkgroupMembershipDecided` (informational, to the requester or added person: join request approved or declined, or a coordinator adds the person directly).
- **Users**: `IUserService` — names, approved-profile check;
  `IUserEmailService` — notification target addresses for lifecycle emails.
- **Governance**: `IGovernanceService.GetMembershipTiersAsync` — roster tier badges and the budget-visibility tier check.
- **Auth**: `IRoleAssignmentService` — active holders of the Board role, the recipients of Board emails.
- **Teams**: `ITeamService` — `SystemTeamIds.Board` membership for badges (controller) and the root Drive readers (Drive source).
- **Calendar**: implements `ICalendarFeedContributor` (inbound) — per-user meetings of
  Active groups the user belongs to; public meetings of Active groups for the community
  calendar window. Calendar names nothing of Workgroups.
- **GoogleIntegration**: implements `IGoogleDriveAccessSource` alongside Teams, using the same reconciliation engine (inbound) — Active group
  folders → ContentManager (`fileOrganizer`) for current members (never a pending request), Dormant → Viewer,
  root → Board readers only. Every claim carries the group's name (`Workgroups` for the root) as the fallback when GoogleIntegration cannot resolve the folder's Drive path, so removal emails and the sync log never show a raw folder id. Every folder-bearing group that is neither Active nor Dormant (Applied, Referred, Refused,
  Withdrawn) remains claimed with an empty roster so existing direct grants are revoked. Calls `IGoogleSyncWrite.CreateSubfolderAsync` (registration) and
  `RequestSyncAsync` (every access-relevant change) outbound.
- **Settings**: `ISettingsService` — legacy root-folder read fallback until the admin saves section-owned settings.
- **Surveys**: `ISurveyAnalysisRead` — `LinkSurveyAsync` reads the posted survey to check the
  actor authored it before writing the log entry. One-way: Surveys never references Workgroups.
- **Notifications, Email, AuditLog**: crosscuts, per Triggers above. Member notifications
  use existing localized labels, grouped by supported recipient language (English fallback); authored content is unchanged.
  A merged-member alias carries both the resolved live id and its language into grouping,
  with one delivery per live recipient even when multiple requested ids resolve to them.
- **Gdpr**: `IUserDataContributor`, `IUserMerge` — see GDPR below.
- **Finance**: `IHoldedFinanceWrite.CreateOrLinkExpenseAccountAsync` /
  `SetExpenseAccountActiveAsync` (outbound). **Holded**: `IHoldedService.GetChartAsync`
  for the admin picker only: active group-6 accounts from the local mirror, with no outbound API call. Account creation and linking still go through Finance.

## GDPR

- **Export** (`ContributeForUserAsync`): six slices — applications (groups this person
  applied for, which is attribution the register keeps on the group row), memberships (role,
  pending flag, dates), log entries, meetings created, documents (created, updated, or with a disposition
  this person recorded — each labelled), comments (including hidden ones, with disposition
  and response). Every attribution column the erasure nulls is also an export predicate, so
  nothing is erasable but unexportable. All exported instants are UTC ISO-8601 text, and log-entry
  dates are ISO dates; absent timestamps remain null.
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
`workgroup_log_entries`, `workgroup_documents`, `workgroup_document_comments`, `workgroups_settings`
**Status:** (A) Migrated — new section, built in this shape from day one.

`MyWorkgroupsViewComponent` supplies the member-dashboard list and passes
`HttpContext.RequestAborted` to its owned `GetForMemberAsync` read.
`GovernanceWorkgroupsViewComponent` does the same for its register and coordinator
display-name reads.

### Cross-section read interface

| Read interface | Methods | Notes |
|---|---:|---|
| `IWorkgroupService` | — | Not published — no other section consumes Workgroups directly (Calendar and GoogleIntegration are inbound fan-outs Workgroups implements); no `.Contracts` leaf |

### For (A) Migrated sections

- `WorkgroupService` lives in `src/Sections/Humans.Workgroups/Services/` and never
  imports `Microsoft.EntityFrameworkCore`.
- `IWorkgroupRepository` (impl `WorkgroupRepository`,
  `IDbContextFactory<WorkgroupsDbContext>`) is the only code path that touches this
  section's tables. Reads no-tracking; the whole register loads as one graph
  (`WorkgroupsGraph`) and the service works over it in memory — a few dozen rows.
- **Decorator decision** — caching decorator (`CachingWorkgroupService`, Singleton). One
  `TrackedCache<byte, IReadOnlyList<WorkgroupInfo>>` inherited by the decorator
  (`Workgroups.Register`), single
  entry for the whole register, cleared on every write. The shared guarded loader prevents
  loads begun before a clear from repopulating it with the old register. Nothing time-derived is cached —
  `WorkgroupRhythm` computes rhythm badges from the snapshot against the caller's clock.
- **Document ordering** — views order documents newest first. The document page sorts comments once: populated categories follow their earliest comment, and each thread keeps chronological order.
- **Display stitching** — `IUserService.GetUserInfosAsync` for burner names; `IGovernanceService.GetMembershipTiersAsync` (in `WorkgroupsController`) for tiers.
- **Cross-section calls** — `IUserService`, `IUserEmailService`,
  `IRoleAssignmentService`, `ISettingsService`, `IGoogleSyncService`, `IGoogleSyncWrite`,
  `INotificationService`, `IEmailService`, `IAuditLogService`,
  `ISurveyAnalysisRead`, `IClock`, `IHoldedFinanceWrite`, `IHoldedService`.
  `ITeamService` is used by `WorkgroupsController` and `WorkgroupDriveAccessSource`, not by `WorkgroupService`.
  `IGovernanceService` is used by `WorkgroupsController` only.
- **Email** — Workgroups owns the working-group notice: `WorkgroupsEmails` (internal)
  builds the `EmailMessage` for each `WorkgroupNoticeKind` from Workgroups' own
  `Workgroups_Email_*` keys in `WorkgroupsResource`, rendered in the recipient's culture
  via `CultureScope`, with English fallback for missing or unsupported saved preferences,
  one `workgroup_notice_<kind>` template name per kind and the Governance opt-out category; `WorkgroupsEmailPreviews` (`IEmailPreviewContributor`,
  registered in `Section.Register`) lists one sample per kind at `/Email/EmailPreview`.
  Email supplies transport only — `IEmailService.SendAsync`
  (`memory/architecture/email-templates-live-in-sender.md`, peterdrier/Humans#1651).
  Email recipient lookup and queue failures are logged without failing a committed
  lifecycle change or preventing its subsequent Drive access sync. Individual sends
  remain independent; cancellation of recipient lookups still propagates.
- **Architecture test** — `tests/Humans.Workgroups.Tests` carries no `Architecture/` folder.
