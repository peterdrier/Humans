# Workgroups — Section Design

**Date:** 2026-09-10 (revised the same day after Peter's two review rounds)
**Status:** Approved shape; implementation follows on this branch. The invariants of record will be `src/Sections/Humans.Workgroups/Docs/Workgroups.md` once built.
**Source of truth:** Board Resolution — Working Groups, adopted 24 August 2026 ([minutes](https://nobodies.team/transparency/2026-08-24-board.html)), plus the Working Group Guidance adopted as Board policy under its clause 6.

> One point is still marked `Implementer decides`; everything else is Peter's call and not up for re-litigation.

---

## 1. Purpose

The register of association-level working groups that the resolution obliges the Secretary to keep, "published to members and collaborators", plus the channel, page and history each group is promised within fourteen days. Humans becomes the register: a group applies here, is registered here, works in the open here (meetings, monthly updates, drafts, a comment period), delivers its output here, and the Board's written disposition is recorded here.

The section is a **register and a record**, not a workflow engine. Registration is administrative recognition and nothing more (clause 4), so the Board's decisions stay human actions with an audit trail, and automation only reminds, flags and records.

Workgroups are **intentionally time-bound**, unlike Teams (departments), which run indefinitely. A recurring subject is a new instance per year ("Finance 2027", "Finance 2028"), not a long-lived group. Workgroups is therefore its own section with its own membership and does **not** wrap a Team.

## 2. Goals

- Apply with exactly what clause 1 asks for: name, purpose, coordinator(s), expected timeline, expected output.
- Secretary (a Board member) registers, refers to the Board, or refuses with written reasons; the Board withdraws with reasons. Every step leaves a log entry and an audit entry.
- Each registered group gets a Drive subfolder under the configured Workgroups root, a Discord channel link, and a page at `/Workgroups/{slug}`.
- A per-group **history log**: system entries for every lifecycle step, member entries for updates, scope changes, disclosures and notes, meetings rendered inline.
- Meetings owned by the group, fed into the community calendar through Calendar's fan-out: members always see them, public ones everyone sees.
- Roster with association status (Board / Asociado / Colaborador / Volunteer) and who coordinates. Anyone signed in may join.
- Documents in Markdown: Draft (members) → Published (all signed-in humans) → comment period with categorised comments and per-comment responses → Delivered → Board disposition.
- Surveys to the membership through Surveys' own approval gate: the group authors, one Board click approves and sends.
- `/Workgroups`: active groups, pending applications, dormant archive.
- The reporting rhythm the guidance asks for, surfaced rather than enforced: update due, quiet for two months, annual report due, awaiting disposition.
- Reachable from `/Governance` (dashboard tile) and from the member home dashboard ("My workgroups"). No main-nav entry.

## 3. Non-goals (v1)

- **No decision-making, voting, or delegated authority.** Clause 4. A group proposes; the Board and Assembly decide elsewhere.
- **No budget.** A group needing money asks the Board separately (two-thirds vote).
- **No Discord sync.** Discord is a link field. The reserved `SyncServiceType.Discord` stays unused.
- **No document versioning or collaborative editing.** One Markdown body, last write wins, no concurrency tokens (`memory/architecture/no-concurrency-tokens.md`). Groups draft in their Drive folder (Google Docs) and paste the publishable Markdown.
- **No document translation.** UI chrome is localized; content is not.
- **No anonymous access.** Signed-in humans with an approved profile only, everywhere.
- **No standing operational teams.** Clause 7: event teams stay in Teams.
- **No mass broadcast from a group.** Surveys through the Surveys gate are the one channel to the membership.
- Global-search contributor and agent knowledge-base entries are follow-ups.

## 4. Concepts & vocabulary

- A **Workgroup** is one register entry: name, purpose, coordinators, target date, deliverable, audience, status, Drive folder, Discord link.
- The **deliverable sentence** is the guidance's "By [date] we will deliver [artefact] to [audience]": `TargetDate`, `Deliverable` (one line), `DeliverableKind`, `Audience` (Board / Assembly / Community).
- A **Coordinator** is one of the one or two people named on the register; the Board appoints whoever the group proposes (clause 3). Technically: a member row with `Role = Coordinator`.
- The **Secretary** is a Board member; every Secretary action is available to `BoardOrAdmin`. No new role.
- A **Member** is any signed-in human who joined. Standing approval (clause 3) means joining is immediate.
- A **Meeting** is a dated session the group owns, optionally public, with minutes filled in afterwards.
- A **Log entry** is one dated line in the group's history. System kinds record lifecycle steps; member kinds record updates, scope changes, disclosures, status requests and notes.
- A **Document** is a Markdown artefact with a Draft → Published → Delivered life, an optional comment period, and, once delivered, the Board's disposition.
- A **Comment** is a signed-in human's remark on a published document during its comment period, tagged with one of the document's categories, and answered by the group with a disposition and response.
- **Dormant** is the terminal status: the group delivered, was closed for silence, or was abandoned. Roster and documents remain readable, the Drive folder goes read-only, nothing else changes. The Secretary may reactivate.

## 5. Actors & roles

| Actor | Capabilities |
|-------|--------------|
| Any signed-in human with an approved profile | Browse the register and every group page; read published documents, meetings and the log; join or leave a group; comment during a comment period; request a status update; apply to form a group |
| Workgroup member | Additionally read Draft documents; edit the register fields; create and edit meetings and minutes; post Update, Disclosure and Note entries; create, edit, publish and deliver documents; open and close comment periods; define comment categories; respond to and dispose of comments; hide a comment with a reason; author a survey in Surveys and submit it for approval; mark the group done |
| Coordinator | Everything a member can, plus: named on the register, addressee of notifications, may hand coordination to another member (1–2 coordinators at all times) |
| Board, Admin (`BoardOrAdmin`) | Register, refer, refuse, withdraw, close, reactivate; set coordinators; register on behalf (bootstrapping); record the Board's disposition; approve-and-send a submitted survey (in Surveys); view the admin queue; edit any group |

Per Peter: **all members can edit for now**; tighten to coordinators later if it proves necessary. The coordinator distinction is register-facing, not permission-facing, in v1.

**Negative rules to verify in tests:**

- A non-member cannot read a Draft document, post a log entry, create a meeting, edit register fields, or reach any document-mutation route.
- A member cannot register, refer, refuse, withdraw, close, reactivate, record a disposition, or approve a survey.
- Nobody can comment outside an open comment period, or on a Draft.
- A Dormant group rejects every member mutation (log, meetings, documents, comments, register edits) with a clear message; only `BoardOrAdmin` actions remain.
- Anonymous requests get the sign-in redirect on every route.

## 6. Lifecycle

```
Applied ──register──▶ Active ◀──reactivate── Dormant
   │                    │                       ▲
   ├──refer──▶ Referred ─┤ (Board decides at its next meeting)
   │                    │
   ├──refuse──▶ Refused ├──withdraw (Board, reasons)──▶ Withdrawn
                        └──done / close ─────────────────┘
```

| Status | Meaning | Who sets it |
|--------|---------|-------------|
| Applied | Notified; waiting on the Secretary (14-day clock from `AppliedAt`) | System, on apply |
| Referred | A refusal ground may apply; the Board decides at its next meeting | BoardOrAdmin |
| Active | Registered. Drive folder created, page and log live | BoardOrAdmin |
| Refused | Registration refused with written reasons | BoardOrAdmin |
| Withdrawn | Registration withdrawn with written reasons (clause 2) | BoardOrAdmin |
| Dormant | Ended. `DormantReason`: Delivered, Abandoned (a member marked it done without delivery), Quiet (closed by the Secretary after the two-month silence) | Member (Delivered/Abandoned), BoardOrAdmin (any) |

Rules:

- Refused and Withdrawn require non-empty `Reasons`, shown on the group page. The log is the written record; the minutes are the Board's.
- Registration creates the Drive subfolder (§9) before the status flips; if creation fails, the group stays Applied and the Secretary sees the error and can retry.
- Dormant flips the Drive folder to read-only through the access source (§9) and freezes the page for members. Reactivation reverses both and writes `Reactivated`. Reactivation is expected to be rare; recurring subjects form a new instance.
- The 14-day clock is a highlight on the admin queue and a notification, never an auto-registration.
- Every transition writes a system log entry and an `AuditLogEntry` with `relatedEntityId = workgroup.Id`, `relatedEntityType = "Workgroup"`.

## 7. Data model

Own project `Humans.Workgroups`, own `WorkgroupsDbContext`, migrations under `Migrations/Workgroups`, context added to `SECTION_DB_CONTEXTS` in `build.yml`. Six tables: `workgroups`, `workgroup_members`, `workgroup_meetings`, `workgroup_log_entries`, `workgroup_documents`, `workgroup_document_comments`. Every cross-section reference is a bare Guid (`memory/architecture/no-cross-section-ef-joins.md`). Instants and dates via NodaTime. Enums string-converted.

### Workgroup — `workgroups`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| Name | string (200) | Register name; year instances put the year in the name |
| Slug | string (100) | Unique; generated from Name, admin-editable |
| Purpose | string (4000) | Markdown, sanitized on render |
| Deliverable | string (500) | One line: the artefact |
| DeliverableKind | WorkgroupDeliverableKind | Recommendation, DraftPolicy, DecisionBrief, Report, AssemblyProposal, ResolutionProposal, DepartmentRegistration, Consultation, Event, Other |
| Audience | WorkgroupAudience | Board / Assembly / Community |
| TargetDate | LocalDate? | Expected delivery, or the event date for Event kinds |
| Status | WorkgroupStatus | §6 |
| DormantReason | WorkgroupDormantReason? | Delivered / Abandoned / Quiet; null unless Dormant |
| DriveFolderId | string (100)? | Google file id of the group's subfolder; null until Active |
| DiscordChannelUrl | string (500)? | |
| Reasons | string (4000)? | Refusal or withdrawal reasons |
| AppliedByUserId | Guid? | Bare Guid; nulled on erasure |
| AppliedAt / RegisteredAt / EndedAt | Instant / Instant? / Instant? | |
| DormantSince | Instant? | Set by the job on 60 days' silence, cleared by the next Update or Meeting; distinct from the Dormant status |
| CreatedAt / UpdatedAt | Instant | |

Indexes: `Slug` unique; `Status`; `DriveFolderId` unique filtered non-null.

### WorkgroupMember — `workgroup_members`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| WorkgroupId | Guid | FK → Workgroup, Cascade |
| UserId | Guid | Bare Guid |
| Role | WorkgroupMemberRole | Member / Coordinator |
| JoinedAt | Instant | |
| LeftAt | Instant? | Soft leave; the roster shows current members, the log keeps history |

Unique filtered `(WorkgroupId, UserId)` where `LeftAt is null`. Invariant: an Active group has 1–2 rows with `Role = Coordinator` and `LeftAt null`; handing over or leaving as the last coordinator requires naming a replacement (Board/Admin can override).

### WorkgroupMeeting — `workgroup_meetings`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| WorkgroupId | Guid | FK → Workgroup, Cascade |
| Title | string (200) | |
| StartUtc / EndUtc | Instant | |
| Location / LocationUrl | string (500)? / string (2000)? | |
| IsPublic | bool | Public meetings appear on everyone's community calendar; otherwise members only |
| Minutes | string? | Markdown, filled in afterwards; "minutes or summarised transcripts" per the guidance |
| CreatedByUserId | Guid? | Bare Guid |
| CreatedAt / UpdatedAt | Instant | |
| DeletedAt | Instant? | Soft delete |

Index `(WorkgroupId, StartUtc)`. Meetings render inline in the log at their date, and as "upcoming" at the top of the group page.

### WorkgroupLogEntry — `workgroup_log_entries`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| WorkgroupId | Guid | FK → Workgroup, Cascade |
| Kind | WorkgroupLogKind | below |
| OccurredOn | LocalDate | Editable on member entries (bootstrapping backdates); system entries use the action date |
| Title | string (200)? | |
| Body | string (16000)? | Markdown, sanitized on render |
| AuthorUserId | Guid? | Bare Guid; null for system entries and after erasure |
| DocumentId | Guid? | FK → WorkgroupDocument, SetNull |
| SurveyId | Guid? | Bare Guid |
| CreatedAt / UpdatedAt | Instant | |

`WorkgroupLogKind`: **system** — Applied, Registered, Referred, Refused, Withdrawn, Ended, Reactivated, CoordinatorChanged, ScopeChanged, MemberJoined, MemberLeft, DormancyInquiry, DocumentPublished, CommentPeriodOpened, CommentPeriodClosed, Delivered, DispositionRecorded, SurveySubmitted, SurveySent; **member** — Update, Disclosure, StatusRequested, Note.

Member entries are editable by any member and deletable by any member (audited); system entries never change. Not §12 append-only by design: the log is a working record, the audit trail is the immutable one.

### WorkgroupDocument — `workgroup_documents`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| WorkgroupId | Guid | FK → Workgroup, Cascade |
| Title | string (200) | |
| Kind | WorkgroupDocumentKind | Deliverable / AnnualReport / Other |
| Body | string | Markdown, unbounded |
| Status | WorkgroupDocumentStatus | Draft / Published / Delivered |
| CommentCategories | jsonb list of string | Defined by the group before opening comments; e.g. "Scope", "Wording", "Timeline" |
| CommentsOpenAt / CommentsCloseAt | Instant? | Window; open when now is inside it |
| DeliveredAt | Instant? | |
| Disposition | WorkgroupDisposition? | Accepted / Declined / Deferred / Noted |
| DispositionNote | string (4000)? | The Board's written reply |
| DispositionAt / DispositionByUserId | Instant? / Guid? | |
| CreatedByUserId / UpdatedByUserId | Guid? | Bare Guids; nulled on erasure |
| CreatedAt / UpdatedAt | Instant | |

Rules: Draft visible to members and admins only. Published requires a non-empty body. A comment window may only be set on a Published document with at least one category, and must end before Delivered. Delivered freezes the body. Disposition only on Delivered. Editing is last-write-wins with "last edited by X at T" shown.

### WorkgroupDocumentComment — `workgroup_document_comments`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| DocumentId | Guid | FK → WorkgroupDocument, Cascade |
| Category | string (100) | One of the document's categories at posting time |
| AuthorUserId | Guid? | Bare Guid; null after erasure, **body kept indefinitely** |
| Body | string (4000) | Markdown |
| CreatedAt | Instant | |
| Disposition | WorkgroupCommentDisposition | Pending / Accepted / Rejected / Incorporated / Noted |
| Response | string (4000)? | The group's answer |
| RespondedByUserId / RespondedAt | Guid? / Instant? | |
| HiddenAt / HiddenByUserId / HiddenReason | Instant? / Guid? / string (500)? | Moderation; hidden comments show "hidden by the group" to everyone, full text to admins |

Comments are grouped by category on the document page. A member responds per comment; a bulk action applies one disposition and response to every Pending comment in a category. Responses are visible to all once set; the resolution's "show what you heard and decided against" is this record.

### Settings

No section table. The Workgroups root folder id is one key, `SettingKeys.WorkgroupsRootDriveFolderId` (`Humans.Settings.Contracts`), read and written through `ISettingsService.GetValueAsync` / `SetValueAsync` like Email, GoogleIntegration and Monitor do. Set on `/Workgroups/Admin/Settings`. Registration is refused with a clear message while unset.

## 8. Calendar fan-out

Calendar's fan-out is extended so sections can feed the **community calendar**, not only the personal iCal feed. Teams will need the same shortly, so the shape is generic:

- `ICalendarFeedContributor` (existing, `Humans.Calendar/Contracts`) gains a second call: public items for a window (`from`, `to`), alongside the existing per-user call. Existing contributors (Shifts, Events) return nothing for the public call until they want to.
- Calendar's month grid, day list and agenda merge contributor items with its own `calendar_events`, marked by `Source`. The personal iCal feed is unchanged.
- Workgroups implements the contributor: per-user items are the meetings of groups the user is a current member of (Active groups only); public items are meetings with `IsPublic = true`.
- Calendar stays the only owner of `calendar_events`; Workgroups owns `workgroup_meetings`. No shared table, no bare-Guid link.

One interface, two methods (Peter's call).

## 9. Drive access through a fan-out

GoogleIntegration gains a Drive access source, mirroring `IGoogleGroupMembershipSource`:

- `IGoogleDriveAccessSource` (in `Humans.GoogleIntegration.Contracts`): a section claims Drive file ids and returns expected access per user, `folderId → (userId → DrivePermissionLevel)`. GoogleIntegration owns email hydration, diffing, mutation, the sync log and reconciliation, exactly as it does for groups. `RequestSyncAsync(folderId)` for on-demand runs; the daily reconciliation covers drift.
- `IGoogleSyncService.CreateSubfolderAsync(parentFolderId, name)` returns the new folder id. New client capability: the Drive client has permissions only today.
- Workgroups implements the source. It claims every Active or Dormant group's `DriveFolderId` and the configured root:
  - group folder, Active: current members → Contributor;
  - group folder, Dormant: current members → Reader (read-only, per Peter);
  - root: Board members, approved Asociados, approved Colaboradors → Reader (from `IUserServiceRead` tiers plus `SystemTeamIds.Board` membership through `ITeamServiceRead`).
- Registration calls `CreateSubfolderAsync(root, workgroup.Name)`, stores the id, then requests a sync. Join, leave and status changes request a sync for the affected folder.

**Teams' own Drive path** (`google_resources` keyed by `TeamId`, reconciled by team membership) is not touched in this work. Migrating it onto the same source fan-out, so GoogleIntegration stops knowing about teams directly, is recorded in `docs/architecture/debt-ledger.yml` as follow-up debt.

## 10. Roster with association status

`workgroup_members` stitched in memory with `IUserServiceRead.GetUserInfosAsync` for `MembershipTier`, plus `SystemTeamIds.Board` membership for the Board badge. Labels: Board, Asociado, Colaborador, Volunteer. Coordinators sort first with a badge. A member's Disclosure entries show as a marker next to their name, linked to the entry. Burner names only.

Join is immediate (standing approval). Leave any time, except the last coordinator (§7). Both write a system log entry and request a Drive sync.

## 11. Surveys, gated

Surveys becomes generically self-service with an approval gate; it never references Workgroups:

- Any signed-in human with an approved profile may create and edit a Draft survey they own (`CreatedByUserId`). They see only their own surveys on `/Survey/Admin`; Board/Admin see all.
- New `SurveyStatus.PendingApproval`. The author submits; Board/Admin get a queue and one action, **Approve and send**, which opens the survey and sends the invitations in one step (Peter: no manual Board steps beyond the approval). Reject returns it to Draft with a note.
- Author-owned surveys may be Identified (Peter's call); the author sees results and exports for their own survey after it closes. Board/Admin see everything as today.
- Workgroups adds a "Surveys" panel on the group page: a member links a survey they authored (by id, from their own list) which writes `SurveySubmitted` when submitted and `SurveySent` when approved. Surveys raises no event to Workgroups in v1; the member posts the link, or the implementer adds a notification hook on approval if it's cheap.

## 12. Documents and the comment period

- Members create documents; members read drafts; members publish. Publishing writes `DocumentPublished` and notifies members.
- Opening a comment period requires categories. It writes a log entry, notifies members, and badges the group "open for comment" on the register. Any signed-in human may comment while open. Closing writes a log entry; comments stay visible read-only.
- Responses: per comment, disposition plus response; bulk by category. Response work may continue after the window closes.
- Deliver: a member marks the document Delivered (body freezes, `Delivered` entry, Board notified). The admin queue lists "awaiting disposition" with days since delivery; the Board owes a written reply by its second meeting after delivery.
- Disposition: BoardOrAdmin records Accepted / Declined / Deferred / Noted with the note; `DispositionRecorded` entry; members notified. Deferred stays in the queue.
- Annual report: a document with Kind = AnnualReport published in a given year satisfies clause 5. The queue flags Active groups registered more than a year ago with no AnnualReport in the last 12 months.

## 13. Reporting rhythm and the daily job

One Hangfire job (`SectionJobs` seam), daily, Active groups only. Every action it takes writes a log entry, an audit entry, and a notification:

| Condition | Action |
|-----------|--------|
| No Update or Meeting in 30 days | Notify coordinators "monthly update due" (in-app, once per window) |
| No Update or Meeting in 60 days and `DormantSince` null | Set `DormantSince`, write `DormancyInquiry`, notify coordinators (email) and Board |
| `DormantSince` older than 14 days, still silent | Notify Board "close candidate"; a human closes with reason Quiet |
| StatusRequested older than 7 days with no later Update | Badge "status overdue" on page and queue |
| Applied older than 14 days | Queue row red; notify Board once |
| Delivered document with no disposition, older than 60 days | Queue row highlighted |

Any Update or Meeting clears `DormantSince`. No automatic closing.

"Request a status update" is a button any signed-in human may press once per group per 7 days; it writes StatusRequested with an optional one-line question and notifies the coordinators.

## 14. Notifications and email

`INotificationService` / `INotificationEmitter` plus the Email crosscut; email only where marked.

| Event | Recipients | Email |
|-------|-----------|-------|
| Applied | Board role | yes |
| Referred | Board role | yes |
| Registered / Refused / Withdrawn / Ended / Reactivated | coordinators | yes |
| Coordinators changed | old and new coordinators | yes |
| Update due (30d) | coordinators | no |
| Dormancy inquiry (60d) | coordinators, Board | yes |
| Status requested | coordinators | no |
| Document published / comment period opened | members | no |
| Comment responded | comment author | no |
| Delivered | Board role | yes |
| Disposition recorded | members | coordinators yes |
| Survey approved and sent (if the Surveys hook lands) | members | no |

## 15. Routes

Member-facing (`[Authorize]`, approved profile), localized in all six cultures:

| Method | Route | Purpose |
|--------|-------|---------|
| GET | `/Workgroups` | Register: Active (badges: open for comment, dormant-flag, status overdue), Pending (Applied/Referred), Archive (Dormant with reason, Withdrawn, Refused with reasons) |
| GET/POST | `/Workgroups/Apply` | Clause 1 form: name, purpose, coordinator(s) (self pre-filled, second optional), target date, deliverable, kind, audience, Discord link |
| GET | `/Workgroups/{slug}` | Group page: header and status; deliverable sentence; coordinators; links (Discord, Drive folder); upcoming meetings; roster; documents; surveys; log with meetings inline; actions by role |
| POST | `/Workgroups/{slug}/Join`, `/Leave` | |
| POST | `/Workgroups/{slug}/RequestStatus` | |
| GET/POST | `/Workgroups/{slug}/Edit` | Members: register fields; changes to deliverable, kind, target date or audience write ScopeChanged |
| POST | `/Workgroups/{slug}/Coordinators` | Members: hand over / add second coordinator |
| GET/POST | `/Workgroups/{slug}/Meetings/Create`, `/{id}/Edit`; POST `/{id}/Delete` | Members; Edit includes minutes |
| GET/POST | `/Workgroups/{slug}/Log/Add`, `/{id}/Edit`; POST `/{id}/Delete` | Members |
| GET | `/Workgroups/{slug}/Documents/{id}` | Read; Draft gated to members; comments grouped by category |
| GET/POST | `/Workgroups/{slug}/Documents/Create`, `/{id}/Edit` | Members |
| POST | `/Workgroups/{slug}/Documents/{id}/Publish`, `/OpenComments`, `/CloseComments`, `/Deliver` | Members |
| POST | `/Workgroups/{slug}/Documents/{id}/Comments` | Any signed-in human, open window only |
| POST | `/Workgroups/{slug}/Comments/{id}/Respond`, `/Hide`; `/Documents/{id}/Comments/RespondCategory` | Members |
| POST | `/Workgroups/{slug}/Done` | Members: Dormant with reason Delivered or Abandoned |
| POST | `/Workgroups/{slug}/Surveys/Link` | Members: attach an authored survey |

Admin (`/Workgroups/Admin/*`, `BoardOrAdmin`, localization-exempt):

| Method | Route | Purpose |
|--------|-------|---------|
| GET | `/Workgroups/Admin` | Queue: pending with day counts, awaiting disposition, dormancy flags, annual report due, status overdue |
| POST | `/Workgroups/Admin/{id}/Register`, `/Refer`, `/Refuse`, `/Withdraw`, `/Close`, `/Reactivate` | Reasons required on Refuse/Withdraw/Close |
| POST | `/Workgroups/Admin/{id}/Coordinators` | Override |
| GET/POST | `/Workgroups/Admin/RegisterExisting` | Bootstrapping: apply on behalf, backdated `RegisteredAt`, immediately Active |
| POST | `/Workgroups/Admin/Documents/{id}/Disposition` | The Board's reply |
| GET/POST | `/Workgroups/Admin/Settings` | Root Drive folder |

Authorization per design-rules §11: `WorkgroupAuthorizationHandler` + `WorkgroupOperationRequirement` (Read, Member, Administer), member resolved from `workgroup_members`, Dormant denying every Member operation.

## 16. Navigation and dashboards

- **No main-nav entry.** Entry points are the Governance page and the member home dashboard.
- New Base seam **`ISectionDashboardTiles`**: sections contribute tiles to a named dashboard (`Governance` for now). `/Governance` renders the slot; Workgroups contributes "Active workgroups" (name, coordinator, deliverable sentence, next meeting, badges). Governance never references Workgroups. If the implementer finds `ISectionChrome`'s slot mechanism already covers this with a new slot name, prefer that over a new interface and say so in the PR.
- **"My workgroups"** on the member home dashboard through the existing `ISectionMemberDashboard` seam: groups the user belongs to, with update-due and open-comment badges; empty state links to the register.
- `ISectionThingsToDo`: "monthly update due" and "status update requested" for coordinators.
- Admin tile "Workgroups" through `ISectionAdminTiles` for `BoardOrAdmin`.

The `governance-scope` memory atom is updated in this PR: Governance is the layer that runs the association itself (statutes, tiers, Board voting, assemblies, working groups), above the event layer everything else serves. Board usage is still audience, not ownership; Workgroups stays its own section and reaches the Governance page only through the seam.

## 17. Cross-section dependencies

| Section | Interface | Use |
|---------|-----------|-----|
| Users | `IUserServiceRead` | names, `MembershipTier`, approved-profile check |
| Teams | `ITeamServiceRead` | Board membership (`SystemTeamIds.Board`) for badges and root Drive readers |
| Calendar | `ICalendarFeedContributor` (extended, §8) — inbound | Workgroups implements it; Calendar names nothing |
| GoogleIntegration | `IGoogleDriveAccessSource` (new, §9) — inbound; `IGoogleSyncService.CreateSubfolderAsync`, `RequestSyncAsync` — outbound | folder creation and sync |
| Settings | `ISettingsService` | root Drive folder id |
| Surveys | none (link by id); Surveys' own changes in §11 | |
| Notifications, Email, AuditLog | crosscuts | §13, §14, audit on every admin and job action |
| Gdpr | `IUserDataContributor` | §18 |

Workgroups is a Section (owns tables). No `.Contracts` leaf until a consumer needs one; don't pre-split.

## 18. GDPR

New personal data: membership rows, log entry authorship and bodies, meeting authorship, document authorship, comments, coordinator role, `AppliedByUserId`.

- **Export:** one `IUserDataContributor` slice: memberships (with role and dates), log entries, meetings created, documents created or updated, comments (including hidden ones, with disposition and response).
- **Erasure:** attribution is nulled everywhere (`AuthorUserId`, `CreatedByUserId`, `UpdatedByUserId`, `RespondedByUserId`, `AppliedByUserId`); **content stays** — comments, log bodies, minutes and documents are the association's record and remain indefinitely (Peter's call). Membership rows are deleted. If the user was the last coordinator of an Active group, the Board is notified.
- **Merge:** fold all user-id columns from source to target; collapse duplicate membership rows.
- **Consent:** nothing new is gated; joining is the member's own action.

## 19. Localization

All member-facing strings in `Humans.Workgroups` resx in en, es, de, it, fr, ca (parity tests). `/Workgroups/Admin/*` exempt. User-authored content is not translated.

## 20. Tests (`tests/Humans.Workgroups.Tests`)

- State machine: every allowed transition and every disallowed one; reasons required; Dormant freezes member mutations; Reactivate restores them.
- Authorization deny paths from §5, including Draft visibility, comment-window gating, last-coordinator leave.
- Job: each row of §13 fires once and clears correctly; every job action audits.
- Document rules: publish requires body; comment window only on Published with categories; Delivered freezes; disposition only on Delivered; bulk category response touches only Pending.
- Drive source: expected access for Active vs Dormant folders and the root; join/leave/status change request a sync.
- Calendar contributor: member meetings per user, public meetings in window, Dormant groups excluded from per-user.
- GDPR contributor: export shape; erasure nulls attribution and keeps content; merge folds ids.
- Registration: subfolder creation failure leaves the group Applied.

Surveys' and Calendar's own changes are tested in their own projects.

## 21. Bootstrapping today's groups

`/Workgroups/Admin/RegisterExisting` creates an Active group with a backdated `RegisteredAt` and creates its subfolder; members then backfill meetings and Update entries with real dates. No import tooling.

## 22. Decisions taken (for the record)

| Topic | Decision |
|-------|----------|
| Team-backed? | No. Own membership; workgroups are time-bound, teams are not |
| Who may apply / join | Any signed-in human with an approved profile; joining is immediate |
| Who edits | All members, for now |
| Secretary | A Board member; `BoardOrAdmin`, no new role |
| Auto-register at day 14 / auto-close dormant | No; flag and notify, a human acts |
| End state | Dormant with reason; Drive read-only; reactivation allowed |
| Comments | In-app, categorised, per-comment disposition and response, body kept forever, attribution erasable |
| Calendar | Calendar fan-out extended for public items; Workgroups owns meetings |
| Drive | Drive access source fan-out in GoogleIntegration plus subfolder creation; Teams path migrates later (ledgered) |
| Surveys | Generic self-service authoring with PendingApproval and one-click approve-and-send; Identified allowed; author sees results |
| Navigation | No main nav; Governance dashboard tile via new `ISectionDashboardTiles`; "My workgroups" via `ISectionMemberDashboard` |
| Anonymous | Never |
