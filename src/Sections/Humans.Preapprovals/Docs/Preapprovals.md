<!-- freshness:triggers
  src/Sections/Humans.Preapprovals/**
  tests/Humans.Preapprovals.Tests/**
-->
<!-- freshness:flag-on-change
  The grant state machine and who moves it, the ceiling check, the claim-deadline semantics
  (a date check at submit, nothing automated), who creates programs vs manages grants, the
  reminder job's send-and-record-only posture, and the GDPR keep-decided-delete-pending
  posture — review when Preapprovals' services, entities or controllers change.
-->

# Preapprovals — Section Invariants

Programs of pre-authorized reimbursements: a pool per event (creativity grants, travel), the
people granted under it, and the state each grant moves through until an expense report
consumes it. Spec: [`docs/features/global/preapprovals.md`](../../../../docs/features/global/preapprovals.md).

## Concepts

- A **Program** is one event-scoped pool: name, Holded account, kind, optional per-person
  amount, optional total ceiling, whether members may apply, the claim deadline, its managers.
  Not `Humans.Events`.
- A **Grant** is one person pre-approved under a program for one amount (`MaxAmount`). A
  grant in `Applied` is a member's request for one; the row is the same.
- A **Manager** is a person who issues, approves, declines and revokes grants in one
  program. Named per program by a finance admin or an admin.
- The **claim deadline** (`ClaimBy`, Europe/Madrid date) is when expense reports against the
  program's grants must be submitted. Checked at submit; nothing runs when it passes.
- **Kind** decides sizing: `FixedPerPerson` (every grant is the program's amount) or
  `PerGrantAmount` (one figure per grant). Locked once the program has a grant.
- **Apply for a pre-approval** is the member action and the UI copy. The bare noun
  "Application" stays reserved for Colaborador/Asociado tier applications.

## Data Model

### PreapprovalProgram — `preapproval_programs`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| Name | string(200) | |
| Description | string(2000)? | |
| Kind | ProgramKind (string 32) | Locked once a grant exists |
| PerPersonAmount | decimal(18,2)? | Required when Fixed, refused when PerGrant |
| TotalCeiling | decimal(18,2)? | Null = no ceiling; edits audited old → new; never below what is committed |
| HoldedAccountNumber | int | Bare external reference, chosen from Finance's registry |
| HoldedAccountId | string(64) | Resolved from the number at save; kept when the number is unchanged |
| AcceptsApplications | bool | |
| ClaimBy | LocalDate | |
| Status | ProgramStatus (string 32) | Open / Closed |
| CreatedByUserId | Guid | Bare cross-section reference |
| CreatedAt / UpdatedAt | Instant | |

Index: `Status`.

### ProgramManager — `preapproval_program_managers`

| Property | Type | Notes |
|----------|------|-------|
| ProgramId | Guid | PK part, FK Cascade |
| UserId | Guid | PK part; bare cross-section reference, indexed |
| AddedByUserId | Guid | |
| AddedAt | Instant | |

### PreapprovalGrant — `preapproval_grants`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| ProgramId | Guid | FK Restrict |
| BeneficiaryUserId | Guid | Bare cross-section reference, indexed |
| Label | string(200)? | The art project; null for travel |
| MaxAmount | decimal(18,2) | Never rewritten by the system |
| Status | GrantStatus (string 32) | See Invariants |
| ApplicationNote | string(2000)? | |
| RequestedAmount | decimal(18,2)? | Per-grant applications only; the manager may approve less |
| DecidedByUserId / DecidedAt | Guid? / Instant? | Null while Applied |
| ExpenseReportId | Guid? | Bare reference to Expenses; set at Claimed, kept after |
| CreatedAt / UpdatedAt | Instant | |

Indexes: `(ProgramId, Status)`, `BeneficiaryUserId`, `ExpenseReportId`.

### ReminderSent — `preapproval_sent_reminders`

| Property | Type | Notes |
|----------|------|-------|
| GrantId | Guid | PK part, FK Cascade |
| Kind | ReminderKind (string 16) | D14 / D3 |
| ClaimBy | LocalDate | PK part: the deadline the reminder announced |
| SentAt | Instant | |

Delivery log only; nothing reads it but the job.

### Enums

| Enum | Values |
|------|--------|
| `GrantStatus` (`Humans.Preapprovals.Contracts`, public) | Applied, Open, Claimed, Consumed, Declined, Revoked |
| `ProgramKind` | FixedPerPerson, PerGrantAmount |
| `ProgramStatus` | Open, Closed |
| `ReminderKind` | D14, D3 |

## Routing

| Route | Controller | Who |
|-------|------------|-----|
| `/Preapprovals`, `/Preapprovals/{programId}` (+ `/Apply` POST) | `PreapprovalsController` | policy `PreapprovalsAccess` |
| `/Preapprovals/Admin`, `/New`, `/{programId}`, `/{programId}/Edit`, `/Issue`, `/IssueMany`, `/Grants/{grantId}/{Approve,Decline,Revoke,Amount}` | `PreapprovalsAdminController` | policy `PreapprovalsManage`; per-program `ProgramOperationRequirement` |

Admin pages live under the section's URL space, not `/Admin`; they are localization-exempt.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Member with a grant | See own grants on `/Preapprovals`; file an expense against an Open one before the deadline. |
| Any member while a program accepts applications | All of the above. Additionally apply for a pre-approval; a second submit edits the pending one. |
| Program manager | All of the above. Additionally, on their programs only: issue, issue to many (fixed kind), approve, decline, revoke, change an amount. Sees only their programs on the admin index. In Expenses, does the first review (endorse, reject, decline a line) of reports filed against their programs' grants. |
| Board | Sees every program and its grants on the admin pages; changes nothing. |
| FinanceAdmin, Admin | All of the program manager's actions on every program. Additionally create and edit programs, set the ceiling, name managers, close a program. |

## Invariants

- Status follows: `Applied → Open | Declined`; `Open → Claimed | Revoked`; `Claimed → Open`
  (withdraw before approval) `| Consumed | Revoked`. Consumed, Declined and Revoked are
  terminal. Every move is a person's action or Expenses' on their behalf; nothing runs on a
  timer (`PreapprovalService`, `ExpensesTransitionTests`).
- Nothing automated changes a grant or program when `ClaimBy` passes. An unclaimed grant
  stays Open with its amount; the member page reads "deadline passed" from the date.
- `MaxAmount` is never zeroed or rewritten by the system; a manager's amount change is
  audited with old and new value (`PreapprovalGrantAmountChanged`).
- Ceiling: Σ `MaxAmount` over Open + Claimed + Consumed never exceeds `TotalCeiling`.
  Checked on issue, issue-many (all or nothing), approve and amount change; a ceiling edit
  below what is committed is refused (`CeilingCheck`, `CeilingBelowCommitted`).
- Fixed kind: every grant is `PerPersonAmount`; an explicit amount is refused on issue and
  approve and ignored on apply. Per-grant kind: an amount is required on issue; an
  application carries `RequestedAmount` and the manager may approve less.
- Amounts and ceilings are positive, at most two decimals, and fit `decimal(18,2)`; anything
  else is refused before a write.
- Issue, issue-many and approve are refused on a Closed program or after `ClaimBy`.
  Applying needs Open, `AcceptsApplications` and the deadline not passed; a POST that
  arrives anyway is 400.
- People are resolved before any write: issuing, applying and naming managers refuse the whole
  request if a person is missing or tombstoned, store the surviving id, and deduplicate merged
  ids (issue-many before its ceiling check). Program-manager checks match every id the viewer
  has held.
- One pending application per person per program across every id they have held; a second
  submit edits it. Applications are serialized by a process-wide lock, so overlapping submits
  edit one row.
- Claim is idempotent for the report already holding the grant and skips the deadline check
  for it (a rejected report resubmitting). Release and consume need the holding report. A
  refusal leaves the grant unchanged; a successful claim returns the grant as booked.
- Audit and notices for a grant change never undo it. Expenses' transitions hand theirs back to
  run after its transaction commits (discarded on rollback); every other change runs them after
  its own save, unaffected by the browser disconnecting. Failures are logged.
- Withdrawal keeps the expense-report id on the grant. Revoking a Claimed grant links the
  holding report in the notice and audit.
- Approve, decline, revoke and amount change take the program id with the grant id and refuse a
  missing or foreign grant before anything changes; the admin routes answer 404, so a manager of
  A never acts on B's grants (`GrantTests.GrantMutation_RefusesMissingOrForeignGrant_WithoutChangingCommitments`,
  `ControllerTests`).
- Malformed grant-action forms are 400 after the permission and ownership checks; a bad amount
  never falls back to another amount, and a bad bulk selection never issues a partial batch.
- The program form needs explicit kind, status and application choices; a missing one
  redisplays the form before anything changes.
- The reminder job sends and writes `preapproval_sent_reminders` rows keyed `(GrantId, Kind,
  ClaimBy)`; it never touches a grant. A moved deadline reminds again (`ReminderTests`).
- Table names start with `preapproval_`. The section's migrations are immutable once shipped.

## Negative Access Rules

- A member **cannot** reach `/Preapprovals/Admin/*` (policy `PreapprovalsManage`).
- A member with no grant, no managed program and no program to apply to **cannot** reach
  `/Preapprovals` (policy `PreapprovalsAccess`). There is no member nav entry: people reach
  the page through notifications, emails and links handed out by managers.
- A program manager **cannot** create or edit a program, set its ceiling, name managers or
  close it (`ProgramOperationRequirement.Administer` is roles only).
- A manager of program A **cannot** manage, issue under, or act on grants of program B
  (`ProgramAuthorizationHandler`, `ControllerTests.GrantOfAnotherProgram_UnderMyProgramsUrl_IsNotFound_EvenWithMalformedInput`).
- A member **cannot** apply to a Closed program, one not accepting applications, or one past
  its deadline.
- Expenses **cannot** claim a grant that is Revoked, Consumed, Claimed by another report, or
  Open past its deadline; it **cannot** release or consume a grant its report does not hold.
- The reminder job **cannot** change any row but `preapproval_sent_reminders`.

## Triggers

- Every state change, program edit, manager add/remove and amount change writes an audit
  entry (`PreapprovalsAuditActions.Preapproval*`), related to the program, the expense report, or the
  affected user. Program fields and manager changes save together or not at all.
- Notices and emails go in the recipient's culture, once per person across merged ids. Audit
  text is invariant.
- Issue / approve: `PreapprovalFileBy` notification (Actionable, source key
  `pre-grant:{id}`) and `preapproval_issued` email to the beneficiary. Claim resolves the
  notification; release re-raises it.
- Apply: `PreapprovalApplicationReceived` to every manager (source key `pre-request:{id}`)
  plus email; approve, decline and a merge-drop resolve it. Each manager notice and email is
  best effort on its own: one failing never skips the others or fails the application.
- Decline, revoke, amount change: `PreapprovalGrantDecided` notification and the matching
  email. A revoke on a Claimed grant links the holding expense report.
- Deadline moved: the stale file-by prompt is resolved, a new High-priority one and an email
  go to every Open holder; reminders run again for the new date.
- Nightly `preapprovals-reminders` (05:00): 14-day and 3-day reminders to Open holders,
  once per `(grant, kind, deadline)`, audited under job name `PreapprovalRemindersJob`. A failed
  notification or email leaves no sent marker; the pass finishes the other grants, then fails
  so Hangfire retries.
- GDPR: export slices `Preapprovals` (own grants, always a list) and `PreapprovalDecisions`
  (created, managed, decided; null when empty), across every id the person has held. Erasure
  deletes Applied and manager rows and keeps every decided grant (Art. 17(3)(b), fiscal
  retention). Merge re-keys every user-id column (including who added a moved manager), keeps
  the original manager-added timestamp, dedups manager rows, and collapses duplicate pending
  applications to the older one with an audit entry.

## Cross-Section Dependencies

- **Audit actions:** declared on `PreapprovalsAuditActions` (`src/Sections/Humans.Preapprovals/PreapprovalsAuditActions.cs`), one `AuditActionDefinition` each, written through `IAuditLogService`: `PreapprovalProgramCreated`, `PreapprovalProgramEdited`, `PreapprovalProgramClosed`, `PreapprovalProgramManagerAdded`, `PreapprovalProgramManagerRemoved`, `PreapprovalGrantIssued`, `PreapprovalGrantApplied`, `PreapprovalGrantApproved`, `PreapprovalGrantDeclined`, `PreapprovalGrantRevoked`, `PreapprovalGrantClaimed`, `PreapprovalGrantReleased`, `PreapprovalGrantConsumed`, `PreapprovalGrantAmountChanged`, `PreapprovalReminderSent`, `PreapprovalMergeDroppedApplication`.
- **Users**: `IUserService.GetUserInfoAsync` / `GetUserInfosAsync` (merged ids, language,
  display and canonical email from the same recipient snapshot); implements `IUserMerge`.
- **Finance**: `IFinanceService.ListExpenseAccountsAsync` to resolve the program's
  account id at save.
- **Notifications**: `INotificationEmitter.SendAsync`, `INotificationAutoResolve.ResolveBySourceKeyAsync`. Its sources are declared in `PreapprovalsNotificationSources` (`src/Sections/Humans.Preapprovals/PreapprovalsNotificationSources.cs`), one `NotificationSourceDefinition` each: `PreapprovalFileBy` (actionable, to the beneficiary: pre-approval issued, released, reminded, or its deadline moved; resolved when the grant leaves Open); `PreapprovalApplicationReceived` (actionable, to the program's managers: someone applies to a pre-approval program; resolved on approve or decline); `PreapprovalGrantDecided` (informational (actionable when a grant is revoked while Claimed by an expense report), to the beneficiary: pre-approval declined, revoked, or its amount changed).
- **Email**: `IEmailService.SendAsync`; `IEmailPreviewContributor` for the seven templates.
- **AuditLog**: `IAuditLogService.LogAsync` (actor and job overloads).
- **Gdpr**: implements `IUserDataContributor`.
- **Expenses** (consumer, PR 3 of the feature): `IPreapprovalWrite.ClaimAsync` / `ReleaseAsync` /
  `ConsumeAsync` inside its own `TransactionScope`; `IPreapprovalService.GetGrantsAsync`.

## Architecture

`PreapprovalService` is a façade over internal components, each owning one job:
`PreapprovalReader` (reads, projection, merged ids, the Madrid date), `PreapprovalClaims` (the
three Expenses transitions), `PreapprovalProgramAdministration` (program create and edit),
`PreapprovalPersonalData` (export, erasure, merge), `PreapprovalJournal` (audit text) and
`PreapprovalInput` (money and text rules). None is registered in DI or public.

Admin manager and batch-recipient lists share `Humans.initHumanSelectionList` in `site.js`.

The scoped internal `PreapprovalNotices` owns recipient resolution, localized delivery and standing-notice completion, shared by grant management and deadline reminders. Ordinary delivery failures remain best-effort. `PreapprovalDeadlineReminders` owns reminder selection, transactional delivery and sent stamps, then audits after commit. The recurring job calls this component directly; it does not resolve grant management.

**Owning services:** `PreapprovalDeadlineReminders` (internal reminder pass), `PreapprovalService` (`Humans.Preapprovals`; `IPreapprovalService` / `IPreapprovalWrite` public in `Contracts/`, `IPreapprovalManagement` internal)
**Owned tables:** `preapproval_programs`, `preapproval_program_managers`, `preapproval_grants`, `preapproval_sent_reminders`
**Status:** (A) Migrated — built as a section from day one (2026-10-16).

### Public contract

A `Contracts/` folder in the section (no leaf project: nothing Preapprovals references reaches Expenses, so no cycle asks for one). Expenses is the only consumer.

| Interface | Methods | Notes |
|---|---:|---|
| `IPreapprovalService` (public) | 1 | `GetGrantsAsync`: every grant, any state, with its holder, its program's managers and whether it can be claimed today. Expenses picks a member's or a manager's grants itself. |
| `IPreapprovalWrite` (public) | 3 | The three Expenses transitions `ClaimAsync` / `ReleaseAsync` / `ConsumeAsync` (returning `PreapprovalWriteResult`, with the grant as booked on a successful claim). Each takes a required `afterCommit` list for its audit and notices. An interface rather than a view component because each must run inside the `TransactionScope` Expenses opens around its own status change. |
| `IPreapprovalManagement : IPreapprovalService, IPreapprovalWrite` (internal) | all the rest | Administration, issuing, applying, approving, declining, revoking. |

`GrantOption`, `PreapprovalWriteResult` and `GrantStatus` are the public types beside them.

### For (A) Migrated sections

- `PreapprovalService` and its components live in `Services/` and never import
  `Microsoft.EntityFrameworkCore`.
- `IPreapprovalRepository` (impl in `Data/`) is the only code path that touches the four
  tables; it hands over detached graphs and takes scalars back.
- **Decorator decision** — no caching decorator. Rationale: a few programs a year, pages
  read by their holders and managers; every read is one query.
- **Display stitching** — `<vc:human>` and `<vc:human-search>` for people on the pages;
  `IUserService.GetUserInfosAsync` only for notice recipients.
- **Cross-section calls** — `IUserService`,
  `IFinanceService`, `INotificationEmitter`, `INotificationAutoResolve`,
  `IEmailService`, `IAuditLogService`.
- **Architecture enforcement** — analyzers HUM0030/32/34/36; `SectionResourceParityTests`
  across every culture; `GdprExportDependencyInjectionTests` for the contributor.
