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
  program. Named per program by the Board, a finance admin or an admin.
- The **claim deadline** (`ClaimBy`, Europe/Madrid date) is when expense reports against the
  program's grants must be submitted. Checked at submit; nothing runs when it passes.
- **Kind** decides sizing: `FixedPerPerson` (every grant is the program's amount) or
  `PerGrantAmount` (one figure per grant). Locked once the program has a grant.
- **Apply for a pre-approval** is the member action and the UI copy. The bare noun
  "Application" stays reserved for Colaborador/Asociado tier applications.

## Data Model

### PreapprovalProgram — `pre_programs`

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

### ProgramManager — `pre_program_managers`

| Property | Type | Notes |
|----------|------|-------|
| ProgramId | Guid | PK part, FK Cascade |
| UserId | Guid | PK part; bare cross-section reference, indexed |
| AddedByUserId | Guid | |
| AddedAt | Instant | |

### PreapprovalGrant — `pre_grants`

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

### ReminderSent — `pre_reminders_sent`

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
| Program manager | All of the above. Additionally, on their programs only: issue, issue to many (fixed kind), approve, decline, revoke, change an amount. Sees only their programs on the admin index. |
| Board, FinanceAdmin, Admin | All of the above on every program. Additionally create and edit programs, set the ceiling, name managers, close a program. |

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
  ignored on approve and apply. Per-grant kind: an amount is required on issue; an
  application carries `RequestedAmount` and the manager may approve less.
- Issue, issue-many and approve are refused on a Closed program or after `ClaimBy`.
  Applying needs Open, `AcceptsApplications` and the deadline not passed; a POST that
  arrives anyway is 400.
- One pending application per person per program across every id they have held; a second
  submit edits it.
- Claim is idempotent for the report already holding the grant and skips the deadline check
  for it (a rejected report resubmitting). Release and consume need the holding report.
- Grant-level admin routes 404 for a grant of another program, so a manager of A never acts
  on B's grants (`PreapprovalsAdminController.ManageAsync`, `ControllerTests`).
- The reminder job sends and writes `pre_reminders_sent` rows keyed `(GrantId, Kind,
  ClaimBy)`; it never touches a grant. A moved deadline reminds again (`ReminderTests`).
- Table names start with `pre_`. The section's migrations are immutable once shipped.

## Negative Access Rules

- A member **cannot** reach `/Preapprovals/Admin/*` (policy `PreapprovalsManage`).
- A member with no grant, no managed program and no program to apply to **cannot** reach
  `/Preapprovals` (policy `PreapprovalsAccess`). There is no member nav entry: people reach
  the page through notifications, emails and links handed out by managers.
- A program manager **cannot** create or edit a program, set its ceiling, name managers or
  close it (`ProgramOperationRequirement.Administer` is roles only).
- A manager of program A **cannot** manage, issue under, or act on grants of program B
  (`ProgramAuthorizationHandler`, `ControllerTests.GrantOfAnotherProgram_UnderMyProgramsUrl_IsNotFound`).
- A member **cannot** apply to a Closed program, one not accepting applications, or one past
  its deadline.
- Expenses **cannot** claim a grant that is Revoked, Consumed, Claimed by another report, or
  Open past its deadline; it **cannot** release or consume a grant its report does not hold.
- The reminder job **cannot** change any row but `pre_reminders_sent`.

## Triggers

- Every state change, program edit, manager add/remove and amount change writes an audit
  entry (`AuditAction.Preapproval*`), related to the program, the expense report, or the
  affected user.
- Member notices and emails format amounts and claim deadlines in the recipient’s culture.
  Amount changes carry both amounts as numbers until rendering; audit amounts remain invariant.
- Issue / approve: `PreapprovalFileBy` notification (Actionable, source key
  `pre-grant:{id}`) and `preapproval_issued` email to the beneficiary. Claim resolves the
  notification; release re-raises it.
- Apply: `PreapprovalApplicationReceived` to every manager (source key `pre-request:{id}`)
  plus email; approve, decline and a merge-drop resolve it. Applicant-name enrichment
  belongs to each best-effort channel: a lookup or delivery fault is logged after the audited
  save, leaves the successful application successful, and does not skip the other channel.
  Caller cancellation still propagates.
- Decline, revoke, amount change: `PreapprovalGrantDecided` notification and the matching
  email. A revoke on a Claimed grant links the holding expense report.
- Deadline moved: the stale file-by prompt is resolved, a new High-priority one and an email
  go to every Open holder; reminders run again for the new date.
- Nightly `preapprovals-reminders` (05:00): 14-day and 3-day reminders to Open holders,
  once per `(grant, kind, deadline)`, audited under job name `PreapprovalRemindersJob`.
- GDPR: export slices `Preapprovals` (own grants, always a list) and `PreapprovalDecisions`
  (created, managed, decided; null when empty). Export follows the resolved identity chain,
  so archived IDs return the same grants and decisions. Erasure deletes Applied and
  manager rows and keeps every decided grant (Art. 17(3)(b), fiscal retention). Merge re-keys every
  user-id column, dedups manager rows, collapses duplicate pending applications to the older
  one with an audit entry.

## Cross-Section Dependencies

- **Users**: `IUserService.GetUserInfoAsync` / `GetUserInfosAsync` (merged ids, language,
  display); `IUserEmailService.GetNotificationTargetEmailsAsync`; implements `IUserMerge`.
- **Finance**: `IHoldedFinanceService.ListExpenseAccountsAsync` to resolve the program's
  account id at save.
- **Notifications**: `INotificationEmitter.SendAsync`, `INotificationAutoResolve.ResolveBySourceKeyAsync`.
- **Email**: `IEmailService.SendAsync`; `IEmailPreviewContributor` for the seven templates.
- **AuditLog**: `IAuditLogService.LogAsync` (actor and job overloads).
- **Gdpr**: implements `IUserDataContributor`.
- **Expenses** (consumer, PR 3 of the feature): `IPreapprovalWrite.ClaimAsync` / `ReleaseAsync` /
  `ConsumeAsync` inside its own `TransactionScope`; `IPreapprovalService.ListClaimableForUserAsync` /
  `GetGrantAsync`.

## Architecture

**Owning services:** `PreapprovalService` (`Humans.Preapprovals`; `IPreapprovalService` / `IPreapprovalWrite` public in `Contracts/`, `IPreapprovalManagement` internal)
**Owned tables:** `pre_programs`, `pre_program_managers`, `pre_grants`, `pre_reminders_sent`
**Status:** (A) Migrated — built as a section from day one (2026-10-16).

### Public contract

A `Contracts/` folder in the section (no leaf project: nothing Preapprovals references reaches Expenses, so no cycle asks for one). Expenses is the only consumer.

| Interface | Methods | Notes |
|---|---:|---|
| `IPreapprovalService` (public) | 2 | `ListClaimableForUserAsync`, `GetGrantAsync`. Returns `GrantOption`, never entities. |
| `IPreapprovalWrite` (public) | 3 | The three Expenses transitions `ClaimAsync` / `ReleaseAsync` / `ConsumeAsync` (returning `GrantStamp`). An interface rather than a view component because each must run inside the `TransactionScope` Expenses opens around its own status change. |
| `IPreapprovalManagement : IPreapprovalService, IPreapprovalWrite` (internal) | all the rest | Administration, issuing, applying, approving, declining, revoking, reminders. |

`GrantOption`, `GrantStamp` and `GrantStatus` are the public types beside them.

### For (A) Migrated sections

- `PreapprovalService` lives in `Services/` as partial files (core, Programs, Grants,
  Notices, Reminders, Gdpr) and never imports `Microsoft.EntityFrameworkCore`.
- `IPreapprovalRepository` (impl in `Data/`) is the only code path that touches the four
  tables; it hands over detached graphs and takes scalars back.
- **Decorator decision** — no caching decorator. Rationale: a few programs a year, pages
  read by their holders and managers; every read is one query.
- **Display stitching** — `IUserService.GetUserInfosAsync` for names on the admin pages;
  `<vc:human>` and `<vc:human-search>` for people.
- **Cross-section calls** — `IUserService`, `IUserEmailService`,
  `IHoldedFinanceService`, `INotificationEmitter`, `INotificationAutoResolve`,
  `IEmailService`, `IAuditLogService`.
- **Architecture enforcement** — analyzers HUM0030/32/34/36; `SectionResourceParityTests`
  for the six cultures; `GdprExportDependencyInjectionTests` for the contributor.
