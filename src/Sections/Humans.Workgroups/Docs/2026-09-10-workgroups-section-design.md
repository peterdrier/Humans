# Workgroups — Section Design

**Date:** 2026-09-10
**Status:** Draft for Peter's review. Implementation follows on this branch; the invariants of record will be `src/Sections/Humans.Workgroups/Docs/Workgroups.md` once built.
**Source of truth:** Board Resolution — Working Groups, adopted 24 August 2026 ([minutes](https://nobodies.team/transparency/2026-08-24-board.html)), plus the Working Group Guidance adopted as Board policy under its clause 6.

> **Open questions for Peter are marked `Q-n`.** Each has a recommendation. Where the answer doesn't change the shape much, the option "implementer decides" is explicit.

---

## 1. Purpose

The register of association-level working groups that the resolution obliges the Secretary to keep, "published to members and collaborators", plus the channel, page and history each group is promised within fourteen days. Humans becomes the register: a group applies here, is registered here, works in the open here (meetings, monthly updates, drafts, a comment period), delivers its output here, and the Board's written disposition is recorded here.

The section is a **register and a record**, not a workflow engine. The resolution is explicit that registration is administrative recognition and nothing more, so the design keeps the Board's decisions as human actions with an audit trail, and lets automation only remind, flag and record.

## 2. Goals

- Apply with exactly what clause 1 asks for: name, purpose, coordinator(s), expected timeline, expected output.
- Secretary registers, refers to the Board, or refuses with written reasons; the Board withdraws with reasons. Every one of those leaves a log entry and an audit entry.
- Each registered group gets a Team (roster, join, Drive folder, calendar), a Discord channel link, and a page at `/Workgroups/{slug}`.
- A per-group **history log**: system entries for every lifecycle step, member entries for meetings (with minutes), monthly updates, scope changes, interest disclosures.
- Upcoming meetings from the community calendar on the group page.
- Roster shows each member's association status (Board / Asociado / Colaborador / Volunteer) and who coordinates.
- Documents in Markdown: Draft (members only) → Published (all signed-in humans) → optional comment period → Delivered → Board disposition recorded.
- Surveys to the membership, gated: a coordinator drafts, the Board opens and sends.
- `/Workgroups`: active groups, pending applications, and a completed/closed archive.
- The reporting rhythm the guidance asks for, surfaced rather than enforced: update due, quiet for two months, annual report due, awaiting Board disposition.

## 3. Non-goals (v1)

- **No decision-making, voting, or delegated authority.** Clause 4. A group proposes; the Board and Assembly decide elsewhere.
- **No budget.** A group needing money asks the Board separately (two-thirds vote). The Team's `HasBudget` flag stays off; Budget integration is a later decision.
- **No Discord sync.** Discord is a link field. The reserved `SyncServiceType.Discord` stays unused.
- **No document versioning or collaborative editing.** One Markdown body, last write wins, no concurrency tokens (`memory/architecture/no-concurrency-tokens.md`). Groups draft long texts in the Drive folder and paste the publishable version.
- **No document translation.** Documents are authored in one language. UI chrome is localized; content is not.
- **No anonymous access.** The register is "published to members and collaborators", so signed-in humans only.
- **No standing operational teams.** Clause 7: event teams are a separate resolution and stay in Teams as they are.
- **No mass broadcast from a group.** Only the Board sends to the membership (surveys, and nothing else in v1).
- Global-search contributor and agent knowledge-base entries are follow-ups, not v1.

## 4. Concepts & vocabulary

- A **Workgroup** is one register entry: name, purpose, coordinators, target date, deliverable sentence, audience, status, and the bare Guid of the Team that backs it once registered.
- The **deliverable sentence** is the guidance's "By [date] we will deliver [artefact] to [audience]", stored as three fields: `TargetDate`, `Deliverable` (one line), `Audience` (Board / Assembly / Community).
- A **Coordinator** is one of the one or two people named on the register. The Board appoints whoever the group proposes (clause 3). Technically: the holders of the management role on the backing Team.
- The **Secretary** is whoever holds the `WorkgroupsAdmin` role (see Q-2). Board and Admin can do everything the Secretary can.
- A **Log entry** is one dated line in the group's history. System-written kinds record lifecycle steps; member-written kinds record meetings, updates, scope changes and disclosures.
- A **Document** is a Markdown artefact owned by the group with a Draft → Published → Delivered life, an optional comment period, and, once delivered, the Board's disposition.
- A **Comment** is a signed-in human's remark on a published document during its comment period.
- **Dormant** is not a status. It is a flag the daily job raises on an Active group with no update or meeting in 60 days, cleared by the next update, acted on by the Secretary.

## 5. Actors & roles

| Actor | Capabilities |
|-------|--------------|
| Any signed-in human with an approved profile | Browse the register and every group page; read published documents and the log; join or leave a group; comment during a comment period; request a status update; apply to form a group; disclose an interest on a group they belong to |
| Workgroup member | Additionally read Draft documents; post Meeting, Update and Disclosure log entries |
| Coordinator (management role on the backing Team) | Additionally edit the register entry (purpose, deliverable, target date, audience, Discord link); create, edit, publish and deliver documents; open and close comment periods; hide a comment with a reason; draft a survey and submit it for approval; mark the group Completed; edit or delete member log entries |
| `WorkgroupsAdmin` (the Secretary), Board, Admin | Register, refer, refuse, withdraw, close; set coordinators; register on behalf (bootstrapping); record the Board's disposition on a delivered document; open and send a submitted survey (Board/Admin via Surveys, unchanged); view the admin queue |

**Negative rules to verify in tests:**

- A non-member cannot read a Draft document, post a log entry, or reach any coordinator route.
- A coordinator cannot register, refuse, withdraw or close their own group, set the disposition, or open/send a survey.
- Nobody can comment outside an open comment period, or on a Draft.
- Anonymous requests get the sign-in redirect on every route.

> **Q-1 — Who may apply?** The resolution says a "group" notifies the Secretary. Options: (a) any signed-in human with an approved profile (recommended: registration is meant to be nearly automatic and the refusal grounds are the gate); (b) Colaborador and up; (c) Asociado only. Recommend (a).

> **Q-2 — The Secretary role.** No `Secretary` role exists in `RoleNames`. Options: (a) new Board-grantable `WorkgroupsAdmin` role, following `RideshareAdmin` / `EETeamAdmin` (recommended: the resolution names an officer, not the whole Board, and the pattern exists); (b) Board-only, no new role. Recommend (a).

## 6. Lifecycle

```
Applied ──register──▶ Active ──deliver+disposition / coordinator marks──▶ Completed
   │                    │
   ├──refer──▶ Referred ─┤ (Board decides at its next meeting: register or refuse)
   │                    │
   ├──refuse──▶ Refused ├──withdraw (Board, reasons)──▶ Withdrawn
                        └──close (Secretary: dormant, abandoned)──▶ Closed
```

| Status | Meaning | Who sets it |
|--------|---------|-------------|
| Applied | Notified; waiting on the Secretary (14-day clock from `AppliedAt`) | System, on apply |
| Referred | A refusal ground may apply; Board decides at its next meeting | Secretary / Board / Admin |
| Active | Registered. Team exists, page and log live | Secretary / Board / Admin |
| Refused | Registration refused with written reasons | Secretary / Board / Admin |
| Withdrawn | Registration withdrawn with written reasons | Board / Admin only |
| Completed | Output delivered and disposed of, or the coordinator declares the work done | Coordinator, or Secretary on disposition |
| Closed | Ended without delivery: quiet for two months with no answer, or abandoned | Secretary / Board / Admin |

Rules:

- Refused and Withdrawn require a non-empty `Reasons` text; it is shown on the group page (the resolution requires written reasons and minuting; the log is the written record, the minutes are the Board's).
- Registration creates the Team (§8) before the status flips; if Team creation fails, the group stays Applied and the Secretary sees the error.
- Completed and Closed deactivate nothing automatically in v1: the Team stays, Drive access stays, the page becomes read-only for members and coordinators (log and documents frozen; admins can still act). See Q-3.
- The 14-day clock is a highlight on the admin queue, not an auto-registration. See Q-4.
- Every transition writes a system log entry and an `AuditLogEntry` with `relatedEntityId = workgroup.Id`.

> **Q-3 — What happens to the Team on Completed/Closed?** (a) leave it (recommended for v1: the roster and Drive are the group's record, and Teams already supports deactivation by Board if wanted); (b) deactivate the Team on close, keeping the Workgroups page as the record; (c) implementer decides. Recommend (a).

> **Q-4 — Auto-register at day 14?** The resolution says the Secretary *shall* register within 14 days; it doesn't say the register does it alone. Recommend no: the queue turns red at day 14 and the Secretary and Board roles get a notification, but a human registers. Alternative: auto-register with a system log entry and an audit entry.

## 7. Data model

Own project `Humans.Workgroups`, own `WorkgroupsDbContext`, migrations under `Migrations/Workgroups`, context added to `SECTION_DB_CONTEXTS` in `build.yml`. Every cross-section reference is a bare Guid (`memory/architecture/no-cross-section-ef-joins.md`). Instants via NodaTime.

### Workgroup — `workgroups`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| Name | string (200) | Register name |
| Slug | string (100) | Unique; generated from Name, editable by admins; also used as the Team's custom slug |
| Purpose | string (4000) | Markdown, sanitized on render |
| Deliverable | string (500) | One line: the artefact |
| Audience | WorkgroupAudience | Board / Assembly / Community (string-converted) |
| TargetDate | LocalDate? | Expected delivery |
| Status | WorkgroupStatus | §6 (string-converted) |
| CoordinatorUserIds | jsonb list of Guid | 1–2 entries; the register value. Mirrored to the Team's management role on registration and on change |
| TeamId | Guid? | Bare Guid; null until Active |
| DiscordChannelUrl | string (500)? | |
| Reasons | string (4000)? | Refusal or withdrawal reasons |
| AppliedByUserId | Guid | Bare Guid |
| AppliedAt / RegisteredAt / ResolvedAt | Instant / Instant? / Instant? | ResolvedAt = Refused/Withdrawn/Completed/Closed |
| DormantSince | Instant? | Set by the job, cleared by the next Update/Meeting entry |
| CreatedAt / UpdatedAt | Instant | |

Indexes: `Slug` unique; `Status`; `TeamId` unique filtered non-null.

### WorkgroupLogEntry — `workgroup_log_entries`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| WorkgroupId | Guid | FK → Workgroup, Cascade |
| Kind | WorkgroupLogKind | see below (string-converted) |
| OccurredOn | LocalDate | Editable on member entries (bootstrapping backdates); system entries use the action date |
| Title | string (200)? | Optional headline (meeting name, update month) |
| Body | string (16000)? | Markdown, sanitized on render; minutes go here |
| AuthorUserId | Guid? | Bare Guid; null for system entries |
| CalendarEventId | Guid? | Bare Guid, Meeting entries only |
| DocumentId | Guid? | FK → WorkgroupDocument (same section), SetNull |
| SurveyId | Guid? | Bare Guid, SurveyRequested/SurveySent entries |
| CreatedAt / UpdatedAt | Instant | |

`WorkgroupLogKind`: **system** — Applied, Registered, Referred, Refused, Withdrawn, Closed, Completed, CoordinatorChanged, ScopeChanged, DormancyInquiry, DocumentPublished, CommentPeriodOpened, CommentPeriodClosed, Delivered, DispositionRecorded, SurveyRequested, SurveySent; **member** — Meeting, Update, Disclosure, StatusRequested, Note.

Not §12 append-only: member entries are editable by their author and the coordinators, deletable by coordinators (audited). System entries are never edited.

### WorkgroupDocument — `workgroup_documents`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| WorkgroupId | Guid | FK → Workgroup, Cascade |
| Title | string (200) | |
| Kind | WorkgroupDocumentKind | Deliverable / AnnualReport / Other (string-converted) |
| Body | string | Markdown, sanitized on render; unbounded (text) |
| Status | WorkgroupDocumentStatus | Draft / Published / Delivered (string-converted) |
| CommentsOpenAt / CommentsCloseAt | Instant? | Both set = comment period defined; open when now is inside the window |
| DeliveredAt | Instant? | |
| Disposition | WorkgroupDisposition? | Accepted / Declined / Deferred / Noted |
| DispositionNote | string (4000)? | The Board's written reply |
| DispositionAt / DispositionByUserId | Instant? / Guid? | |
| CreatedByUserId / UpdatedByUserId | Guid | Bare Guids |
| CreatedAt / UpdatedAt | Instant | |

Rules: Draft is visible to members, coordinators and admins only. Published requires a non-empty body. A comment period may only be set on a Published document and must end before Delivered. Delivered freezes the body. Disposition is recordable only on Delivered.

### WorkgroupDocumentComment — `workgroup_document_comments`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| DocumentId | Guid | FK → WorkgroupDocument, Cascade |
| AuthorUserId | Guid? | Bare Guid; null after erasure |
| Body | string (4000) | Markdown, sanitized on render |
| CreatedAt | Instant | |
| HiddenAt / HiddenByUserId / HiddenReason | Instant? / Guid? / string (500)? | Coordinator moderation; hidden comments show "hidden by a coordinator" to everyone, full text to admins |

> **Q-5 — Comment period: in-app or Discord?** (a) in-app comments as above (recommended: the guidance expects a proposal to show "who was consulted, how, and what came back", and a durable record attached to the document is that); (b) a Discord thread link per document, no comments table, no moderation, no GDPR surface. Recommend (a).

### WorkgroupSettings — `workgroup_settings` (singleton row)

`ParentTeamId` (Guid?): the "Working Groups" department every backing Team is created under. Set once on `/Workgroups/Admin/Settings`. Registration is refused with a clear message while it is unset.

> **Q-6 — Settings home.** If `Humans.Settings` already offers a typed per-section key/value store, use it instead of this table. Implementer decides after reading `src/Sections/Humans.Settings/Docs`.

## 8. The backing Team

Each Active workgroup has exactly one Team, a **sub-team** of the "Working Groups" department, created by Workgroups through `ITeamService.CreateTeamAsync` (`[CrossSectionWrite]` per `memory/architecture/section-read-write-split.md`) with `requiresApproval = false` (clause 3 standing approval), the workgroup's slug, no Google group prefix, not hidden.

Why a Team, and why a sub-team:

- Roster, join/leave, Drive folder linking and sync, calendar ownership, team-scoped audit and budget all key off a TeamId today. A standalone membership model would rebuild all of it.
- A sub-team keeps the Teams directory clean (one "Working Groups" department; sub-teams reachable from it) and makes every workgroup member a member of the department, which is where a shared "Working Groups" Drive root belongs.
- The coordinator becomes the sub-team's **manager** (the management role definition, two slots, created programmatically on registration and assigned from `CoordinatorUserIds`). Managers are not added to the Coordinators system team and cannot manage Google resources, which matches clause 4: no authority beyond the group.

Consequences the implementer must handle:

- `ITeamService` exposes no public join/leave or add/remove member methods (`AddSeededMemberAsync` is seed-only). v1 join/leave on the group page posts to Teams' own `/Teams/{slug}/Join` and `/Teams/{slug}/Leave` with a return URL, or Teams gains a narrow `JoinAsync/LeaveAsync` on `ITeamService`. **New public surface on Teams needs Peter's approval — see Q-7.**
- Creating the management role definition and assigning it also needs `ITeamService` methods; check `CreateRoleDefinitionAsync` / `AssignToRoleAsync` exist on the contract, and add them narrowly if not (same Q-7).
- Drive: the department's coordinator (the Secretary) or TeamsAdmin links each group's folder under the Working Groups shared drive via the existing `/Teams/{slug}/Resources`. No auto-provisioning in v1 (there is no folder-create capability in `IGoogleSyncService`; groups get provisioned). The group page shows the linked resources through `ITeamResourceService.GetTeamResourcesAsync`.
- The sub-team manager can still reach `/Teams/{slug}/Members` and remove members. Workgroups does not expose removal; standing approval means members may participate, so removal is a Board matter. Accept the Teams-side reach in v1.

> **Q-7 — Teams surface additions.** Expected: `JoinAsync`, `LeaveAsync`, and whatever role-definition/assignment write Workgroups needs, on `ITeamService`. Alternative: keep join/leave on Teams' routes (no new surface, slightly clunkier UX). Recommend the narrow additions; you approve them here or the implementer falls back to the routes.

## 9. Calendar

Meetings are `CalendarEvent`s owned by the backing Team. The group page offers "Schedule a meeting" (`/Calendar/Event/Create?teamId=`) and "Full calendar" (`/Calendar/Team/{teamId}`).

"Upcoming meetings" on the group page needs a read Calendar doesn't expose today: `ICalendarServiceRead` is internal and nothing outside the section reads an event. Proposal: promote a DTO-only `ICalendarServiceRead.GetUpcomingForTeamAsync(teamId, from, to)` to `Humans.Calendar/Contracts` (folder, no leaf: Workgroups is the only consumer). Calendar already holds the `CalendarEventInfo` projection and the occurrence expander, so this is exposure, not new logic.

> **Q-8 — Calendar read surface.** (a) add the read above (recommended: you asked for upcoming meetings on the page); (b) v1 links only, upcoming list later. Recommend (a).

A Meeting log entry is posted by a member after the fact, with minutes in the body and the `CalendarEventId` picked from the team's recent events. Nothing is written automatically when an occurrence passes.

## 10. Roster with association status

Rendered on the group page from `ITeamServiceRead.GetTeamAsync(teamId).Members` stitched in memory with `IUserServiceRead.GetUserInfosAsync` for `MembershipTier`, plus membership of `SystemTeamIds.Board` for the Board badge. Labels: Board, Asociado, Colaborador, Volunteer. Coordinators carry a badge and sort first. Disclosures (§7, Kind = Disclosure) show as a small marker next to the member with the entry linked.

Visible to signed-in humans only, like everything else here. No burner-name/legal-name split: burner name only, as on the public team page.

## 11. Surveys, gated

Recommended shape, **Option A — Surveys gains a team owner**:

- `Survey.OwningTeamId` (nullable bare Guid; Surveys already references `Humans.Teams.Contracts`).
- `/Survey/Admin` authoring (Create, Edit, Preview, Save) additionally allowed for the management-role holders of `OwningTeamId` on surveys with that owner. **Open, Send, Close, Results and exports stay Board/Admin.** That is the gate: a coordinator can build, nobody but the Board can send.
- A coordinator-owned survey may not be Identified (`AllowAnonymous` forced; anonymity tier restricted to CompletionTracked or Anonymous) so that authoring access never becomes personal-data access.
- Workgroups adds "Submit for approval": writes a `SurveyRequested` log entry with the `SurveyId`, notifies Board and `WorkgroupsAdmin`. When the Board opens and sends, Workgroups isn't told; the coordinator posts a `SurveySent` entry, or the implementer adds a Surveys-side notification hook later. Results reach the group as a Board-shared export, or via a later "coordinator may read aggregate results" extension.

**Option B — request only**: the coordinator writes the survey request as a document, the Board authors and sends in Surveys, links back by pasting the survey URL into a log entry. Zero Surveys change; the group never "makes" the survey.

> **Q-9 — Survey gating.** You said groups make surveys; A does that with a real gate. B is much smaller. Recommend A, with the Identified restriction. Confirm, and confirm whether coordinators should be able to read aggregate results of their own survey in v1 (recommend no: Board shares results; keeps Surveys authorization change to authoring only).

## 12. Documents and the comment period

- Coordinators create documents; members read drafts; coordinators publish. Publishing writes a `DocumentPublished` log entry and notifies the group's members.
- A comment period is a window on a Published document. Opening it writes a log entry and notifies group members; the register index badges the group "open for comment". Any signed-in human may comment while it is open. Closing writes a log entry. Comments stay visible afterwards, read-only.
- Deliver: coordinator marks the document Delivered (body freezes, `Delivered` log entry, Board and Secretary notified). The admin queue lists "awaiting disposition" with days since delivery, since the Board owes a written reply by its second meeting after delivery.
- Disposition: Secretary/Board record Accepted / Declined / Deferred / Noted with the written note; `DispositionRecorded` log entry; coordinators and members notified. Deferred keeps the item in the queue with the note.
- Annual report: a document with Kind = AnnualReport published in a given year satisfies clause 5 for that year. The queue flags Active groups whose `RegisteredAt` is more than a year past with no AnnualReport published in the last 12 months.

Editing is last-write-wins with "last edited by X at T" shown; no concurrency tokens. Coordinators are told to draft in Drive.

## 13. Reporting rhythm and the daily job

One Hangfire job (registered through the `SectionJobs` seam), daily, Active groups only. It acts only on what it can derive from the log, and every action it takes writes a log entry, an audit entry, and a notification:

| Condition | Action |
|-----------|--------|
| No Update or Meeting entry in 30 days | Notify coordinators "monthly update due" (in-app only, once per 30-day window) |
| No Update or Meeting entry in 60 days and `DormantSince` null | Set `DormantSince`, write `DormancyInquiry` entry, notify coordinators (email + in-app) and Secretary |
| `DormantSince` older than 14 days and still no entry | Notify Secretary "close candidate"; the Secretary closes by hand |
| StatusRequested entry older than 7 days with no later Update | Badge "status overdue" on the page and queue; no further nagging |
| Applied older than 14 days | Queue row turns red; notify Secretary and Board once |
| Delivered document with no disposition, older than 60 days | Queue row highlighted (a proxy for "two Board meetings") |

Any Update or Meeting entry clears `DormantSince`.

> **Q-10 — Auto-close dormant groups?** The guidance says "closed if no answer comes". Recommend the job flags and a human closes (register hygiene stays a Secretary act, matching how refusal and withdrawal are human acts). Alternative: auto-close at day 74 with system entries.

"Request a status update" is a button any signed-in human can press once per group per 7 days; it writes a StatusRequested entry with an optional one-line question and notifies the coordinators. This is the guidance's "any member may ask" made concrete.

## 14. Notifications and email

Through `INotificationService` (role targets) / `INotificationEmitter` (known recipients) and the Email crosscut; email only where marked.

| Event | Recipients | Email |
|-------|-----------|-------|
| Applied | `WorkgroupsAdmin` role | yes |
| Referred | Board role | yes |
| Registered / Refused / Withdrawn / Closed | coordinators | yes |
| Coordinators changed | old and new coordinators | yes |
| Update due (30d) | coordinators | no |
| Dormancy inquiry (60d) | coordinators, Secretary | yes |
| Status requested | coordinators | no |
| Document published / comment period opened | group members | no |
| Delivered | Board role, Secretary | yes |
| Disposition recorded | coordinators, group members | coordinators yes |
| Survey submitted for approval | Board role, Secretary | yes |

## 15. Routes

Member-facing (`[Authorize]`, approved profile), localized in all six cultures:

| Method | Route | Purpose |
|--------|-------|---------|
| GET | `/Workgroups` | Register: Active (with badges: open for comment, dormant, status overdue), Pending (Applied/Referred), Archive (Completed, Closed, Withdrawn, Refused with reasons) |
| GET/POST | `/Workgroups/Apply` | Clause 1 form: name, purpose, coordinator(s) (self pre-filled, second optional), target date, deliverable, audience, Discord link |
| GET | `/Workgroups/{slug}` | Group page: header and status; deliverable sentence; coordinators; links (Discord, Drive resources, calendar); upcoming meetings; roster; documents; log (newest first); actions by role |
| POST | `/Workgroups/{slug}/Join`, `/Leave` | Via Teams (Q-7) |
| POST | `/Workgroups/{slug}/RequestStatus` | StatusRequested entry |
| GET/POST | `/Workgroups/{slug}/Edit` | Coordinator: register fields; any change to deliverable/target date/audience writes ScopeChanged |
| GET/POST | `/Workgroups/{slug}/Log/Add`, `/Log/{id}/Edit`, POST `/Log/{id}/Delete` | Member/coordinator entries |
| GET | `/Workgroups/{slug}/Documents/{id}` | Read; Draft gated to members |
| GET/POST | `/Workgroups/{slug}/Documents/Create`, `/{id}/Edit` | Coordinator |
| POST | `/Workgroups/{slug}/Documents/{id}/Publish`, `/OpenComments`, `/CloseComments`, `/Deliver` | Coordinator |
| POST | `/Workgroups/{slug}/Documents/{id}/Comments` | Any signed-in human, open period only |
| POST | `/Workgroups/{slug}/Comments/{id}/Hide` | Coordinator, reason required |
| POST | `/Workgroups/{slug}/Complete` | Coordinator |
| POST | `/Workgroups/{slug}/Surveys/{surveyId}/Submit` | Coordinator (Option A) |

Admin (`/Workgroups/Admin/*`, policy `WorkgroupsAdminBoardOrAdmin`, localization-exempt):

| Method | Route | Purpose |
|--------|-------|---------|
| GET | `/Workgroups/Admin` | Queue: pending with day counts, awaiting disposition, dormant, annual report due, status overdue |
| POST | `/Workgroups/Admin/{id}/Register`, `/Refer`, `/Refuse`, `/Withdraw` (Board/Admin), `/Close` | Reasons required on Refuse/Withdraw/Close |
| POST | `/Workgroups/Admin/{id}/Coordinators` | Set 1–2; mirrors to the Team's management role |
| GET/POST | `/Workgroups/Admin/RegisterExisting` | Bootstrapping: apply on behalf with a backdated `RegisteredAt`, immediately Active |
| POST | `/Workgroups/Admin/Documents/{id}/Disposition` | Record the Board's reply |
| GET/POST | `/Workgroups/Admin/Settings` | Parent department |

Authorization is resource-based per design-rules §11: `WorkgroupAuthorizationHandler` + `WorkgroupOperationRequirement` (Read, Member, Coordinate, Administer), with coordinator resolved from the Team's management-role holders.

## 16. Navigation

- Main nav "Workgroups" (`SectionNav` seam) for signed-in humans.
- Admin tile "Workgroups" (`SectionAdminTiles` seam or whatever the seam is named) for the admin policy.
- Group page ↔ Team page ↔ calendar cross-links. Team page of a workgroup-backed team shows "This team backs the workgroup …" only if Teams offers a slot for it; otherwise skip (no Teams edit for a link).
- Home dashboard widget "your workgroups" is a follow-up.

## 17. Cross-section dependencies

| Section | Interface | Use |
|---------|-----------|-----|
| Teams | `ITeamServiceRead`, `ITeamService` (`[CrossSectionWrite]`) | roster, coordinator check, create team, role/assign, join/leave (Q-7) |
| Users | `IUserServiceRead` | names, `MembershipTier`, approved-profile check |
| Calendar | new `ICalendarServiceRead` read (Q-8) | upcoming meetings |
| GoogleIntegration | `ITeamResourceService.GetTeamResourcesAsync` | Drive links on the page |
| Surveys | none from Workgroups (link by id); Surveys changes are inside Surveys (Q-9) | |
| Notifications, Email, AuditLog | crosscuts | §13, §14, audit on every admin/job action |
| Gdpr | `IUserDataContributor` | §18 |

Workgroups is a Section (owns tables), not an orchestrator. No `.Contracts` leaf until something needs one; don't pre-split.

## 18. GDPR

New personal data: log entry authorship and bodies, document authorship, comments, disclosures, coordinator ids, `AppliedByUserId`.

- **Export:** one `IUserDataContributor` slice: memberships are Teams' concern; Workgroups exports the user's log entries, documents created/updated, comments (including hidden ones), and groups they applied for or coordinate.
- **Erasure:** comments by the user are deleted (they are personal opinion, not the association's record); log entries and documents keep their content with `AuthorUserId` / `CreatedByUserId` / `UpdatedByUserId` set to null (the group's record survives, attribution doesn't); the user is removed from `CoordinatorUserIds` and `AppliedByUserId` is nulled, and the Secretary is notified if a group loses its last coordinator.
- **Merge:** fold all user-id columns from source to target.
- **Consent:** nothing new is gated; joining a group is a member's own action.

## 19. Localization

All member-facing strings in `Humans.Workgroups` resx in en, es, de, it, fr, ca (parity tests). `/Workgroups/Admin/*` exempt. Document and log content is user-authored and not translated.

## 20. Tests (`tests/Humans.Workgroups.Tests`)

- State machine: every allowed transition and every disallowed one (e.g. Refused → Active is not a path; Withdrawn only by Board/Admin; reasons required).
- Authorization deny paths from §5, including Draft visibility and comment-period gating.
- Job: each row of §13 fires once and clears correctly; every job action audits.
- Document rules: publish requires body; comment window only on Published; Delivered freezes; disposition only on Delivered.
- GDPR contributor: export shape; erasure nulls attribution and deletes comments; merge folds ids.
- Team creation on registration: failure leaves the group Applied.

## 21. Bootstrapping today's groups

`/Workgroups/Admin/RegisterExisting` creates an Active group with a backdated `RegisteredAt`, then coordinators backfill Meeting/Update entries with their real `OccurredOn` dates. No import tooling.

## 22. Decision summary for Peter

| Q | Decision needed | Recommendation |
|---|-----------------|----------------|
| Q-1 | Who may apply | Any signed-in human with an approved profile |
| Q-2 | Secretary role | New `WorkgroupsAdmin` role |
| Q-3 | Team after Completed/Closed | Leave it |
| Q-4 | Auto-register at day 14 | No; highlight and notify |
| Q-5 | Comment period | In-app comments |
| Q-6 | Settings storage | Implementer decides after reading Settings |
| Q-7 | Teams surface: join/leave, role write | Approve narrow additions |
| Q-8 | Calendar read surface | Approve `GetUpcomingForTeamAsync` |
| Q-9 | Survey gating | Option A, no Identified surveys, results stay Board-only |
| Q-10 | Auto-close dormant | No; flag, human closes |
