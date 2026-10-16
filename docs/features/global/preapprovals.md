<!-- freshness:triggers
  src/Sections/Humans.Preapprovals/**
  src/Sections/Humans.Preapprovals.Contracts/**
  src/Sections/Humans.Expenses/Services/ExpenseReportService.cs
  src/Sections/Humans.Expenses/Domain/ExpenseReport.cs
  src/Sections/Humans.Finance.Contracts/**
-->
<!-- freshness:flag-on-change
  Program kinds, the per-program total ceiling, the claim deadline semantics, which role creates
  programs vs manages grants, and how a grant stamps and locks an expense report — review when
  Preapprovals' model, Expenses' cap/account model, or Finance's account registry change.
-->

# Pre-approvals — programs of pre-authorized reimbursements

Several times a year the association authorizes spending **before** the receipts exist: the
creativity budget for an event edition (art projects, each led by one person, each allowed a
stated amount) and travel reimbursements for an event (a fixed amount per person, 250 € for the
Oct 2026 offsite, ~50 people). Today both are either managed by hand or not at all, and the
expense report that follows is reviewed as if nothing had been agreed.

A **Program** is one such pool for one event: its Holded account, its rules, optionally its total
ceiling, its managers, and its claim deadline. A **Grant** is one person authorized under the
program for one amount. A grant turns into an expense report that is capped at the grant and
booked to the program's account without anyone re-deciding either.

Design settled with Peter, 2026-10-07. Spec only; no code yet.

## Terminology

| Term | Meaning |
|------|---------|
| Program | One event-scoped pool of pre-approvals. "2026 Creativity", "Oct 2026 Offsite Travel". Not `Humans.Events`, which is the event guide. |
| Grant | One person pre-approved under a program for one amount. The row the member files an expense against. |
| Manager | A person allowed to issue, approve and revoke grants in one program. Named per program by the Board or a finance admin. |
| Claim deadline | The program's `ClaimBy` date. Expense reports against its grants must be **submitted** by then. Prominent everywhere; no grace. A date check at submit, nothing more: nothing runs when it passes and nothing changes state. |
| Apply for a pre-approval | A member asking to be granted under a program. Always "apply **for a pre-approval**" in UI copy and docs; the bare noun `Application` stays reserved for Colaborador/Asociado tier applications (Peter, PR 1935). |

## Ownership

| Fact | Owner | Why |
|------|-------|-----|
| Programs, grants, applications, managers | Preapprovals (new section) | A pre-approval has its own lifecycle and is written by several callers (admins, applicants, later Creativity). It is not an expense and not a budget line. |
| Which Holded account a program books to | Preapprovals (`HoldedAccountNumber` / `HoldedAccountId` on the program, no FK) | Same posture as Workgroups: an opaque external id chosen from Finance's registry. |
| Account creation, naming, dedup | Finance | Already owns it (`CreateOrLinkExpenseAccountAsync`, `ListExpenseAccountsAsync`). |
| Cap and account on the report, payable math, Holded push | Expenses | Unchanged: `MaxAmount` and `PayableAllocation` already do this. The grant only sets them up front. |
| Consuming a grant | Preapprovals, on Expenses' call | Expenses tells Preapprovals "report X was submitted / approved / withdrawn / rejected against grant G"; Preapprovals moves the grant. Expenses never writes `preapprovals` rows. |

Nobody outside Preapprovals learns what a program is. Expenses sees a grant as
`(Id, Label, MaxAmount, HoldedAccountNumber, HoldedAccountId, ClaimBy)`.

**Prerequisite:** [workgroup-budget Phase 2](workgroup-budget.md#phase-2--expenses-separate-pr-after-phase-1-is-on-qa)
(an expense report names its Holded account, not a budget category). Without it a grant's
account has nowhere to go on the report. Phase 2 ships first, on its own PR.

## Data model (`PreapprovalsDbContext`, all tables owned by `Humans.Preapprovals`, prefixed `pre_`)

### `pre_programs`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| Name | string(200) | "2026 Creativity" |
| Description | string(2000)? | Shown on the public program page and the apply form |
| Kind | enum `ProgramKind` | `FixedPerPerson` (every grant is `PerPersonAmount`) / `PerGrantAmount` (amount set on each grant) |
| PerPersonAmount | decimal(18,2)? | Required when `FixedPerPerson`; refused otherwise. Must be greater than zero |
| TotalCeiling | decimal(18,2)? | Null = no ceiling. Creativity: 25 000 → 50 000 → 60 000, edited in place; each edit audited so the history is in the log |
| HoldedAccountNumber | int | From `IHoldedFinanceServiceRead.ListExpenseAccountsAsync(activeOnly: true)` |
| HoldedAccountId | string(64) | |
| AcceptsApplications | bool | Public apply page open |
| ClaimBy | LocalDate | Claim deadline. Required. Reports must be submitted on or before this date (Europe/Madrid, end of day). Editable at any time by Board / FinanceAdmin, audited with old and new value. The edit also resolves each `Open` grant holder's `pre-grant:{id}` notification and emits a new one with the new date, so the stale prompt never sits beside the replacement. Reminder markers carry the deadline they announced (see `pre_reminders_sent`), so the reminders run again against the new date with nothing to clear. Pushing it back needs nothing undone: an `Open` grant past the old date is simply claimable again |
| Status | enum `ProgramStatus` | `Open` / `Closed`. Closed: no new grants, no applications, the apply page and form are hidden and the POST refused whatever `AcceptsApplications` says; existing `Open` grants stay claimable until `ClaimBy`. Closing freezes the numbers: nothing is zeroed or rewritten |
| CreatedByUserId, CreatedAt, UpdatedAt | | |

### `pre_program_managers`

| Property | Type | Notes |
|----------|------|-------|
| ProgramId | Guid | FK → `pre_programs` |
| UserId | Guid | Bare Guid (Users), no FK |
| AddedByUserId, AddedAt | | |

PK `(ProgramId, UserId)`. One role; a manager is a manager.

### `pre_reminders_sent`

| Property | Type | Notes |
|----------|------|-------|
| GrantId | Guid | FK → `pre_grants` |
| Kind | enum `ReminderKind` | `D14` / `D3` |
| ClaimBy | LocalDate | The deadline the reminder announced |
| SentAt | Instant | |

PK `(GrantId, Kind, ClaimBy)`. The reminder job's delivery log, so a retried run cannot send a
reminder twice for the same deadline. The job re-reads the program's `ClaimBy` inside the
per-grant transaction and stamps that value, so a concurrent deadline edit can never leave a
marker that suppresses the reminder for the new date. Not grant state; nothing reads it but the
job.

### `pre_grants`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| ProgramId | Guid | FK |
| BeneficiaryUserId | Guid | Bare Guid |
| Label | string(200)? | The art project name; null for travel |
| MaxAmount | decimal(18,2) | Copied from `PerPersonAmount` for `FixedPerPerson`; entered for `PerGrantAmount`. Must be greater than zero (service-side, every path: issue, approve, edit); the ceiling sum is only meaningful with positive rows. Editable by a program manager or above while the grant is `Open` or `Claimed` (1 000 becomes 1 500), audited with old and new value and checked against the ceiling. Never rewritten by the system: a revoked or unclaimed grant keeps its amount, so "6 000 of 25 000 was not claimed" is readable later |
| Status | enum `GrantStatus` | `Applied` / `Open` / `Claimed` / `Consumed` / `Declined` / `Revoked` |
| ApplicationNote | string(2000)? | What the applicant wrote; null when issued directly |
| RequestedAmount | decimal(18,2)? | What the applicant asked for (`PerGrantAmount` programs), greater than zero; the manager may approve less |
| DecidedByUserId, DecidedAt | | Who approved / declined / issued |
| ExpenseReportId | Guid? | Bare Guid (Expenses). Set at `Claimed`, kept at `Consumed` and after the report is withdrawn |
| CreatedAt, UpdatedAt | | |

A grant and an application are the same row: an application is a grant in `Applied`.

## Grant state machine

```
Applied ──approve──▶ Open ──report submitted──▶ Claimed ──report approved──▶ Consumed
   │                  │                            │
   └──decline──▶ Declined                          └──report withdrawn before approval──▶ Open
                      │
                      └──revoke──▶ Revoked
```

- `Open` is the only state a member can file against. Claimable = grant `Open` and the
  program's `ClaimBy` not passed **today**. The program's own `Status` does not matter: closing a
  program stops new grants and applications, never an issued grant.
- `Claimed` holds the grant while the report is in flight so it cannot be used twice. One grant,
  one report. `ClaimAsync` is idempotent for the report already holding the grant, which is what
  a rejected report (back in `Draft`) needs when it is resubmitted.
- `Consumed` is terminal, and so are `Declined` and `Revoked`. Every terminal state keeps the
  grant's amount and its report id; the state is frozen, the numbers are not touched.
- **There is no expiry state and no job.** An `Open` grant past `ClaimBy` stays `Open`; it is
  shown as "deadline passed" and cannot be filed against. Nothing automated runs when the date
  passes, so pushing `ClaimBy` back makes those grants claimable again with nothing to undo.
- Revoke is allowed on `Open` and `Claimed`. Revoking a `Claimed` grant does not touch the
  report: the next `ClaimAsync` (resubmit) and `ConsumeAsync` (approve) for it are refused with
  "pre-approval revoked", so a submitted report must be rejected by Finance and lands in
  `Draft`. A `Draft` whose grant is revoked is not stranded: the header edit lets the member
  drop the grant (the report becomes an ordinary one, account or category picked as usual, no
  cap) or pick another claimable grant, then submit normally. `Consumed` is never revoked.
- **Atomicity.** `ClaimAsync`, `ReleaseAsync` and `ConsumeAsync` run inside the ambient
  `TransactionScope` Expenses opens around its own status change, the pattern
  `AccountMergeService` and `CampService` already use across contexts. Either both commit or
  neither; there is no "report withdrawn, grant still claimed" state to repair.

### What Expenses' transitions do to the grant

| Report event | Report status after | Grant |
|--------------|---------------------|-------|
| Submit (first time) | Submitted | `Open → Claimed`, `ExpenseReportId` set, cap and account stamped **at this moment** from the grant's current program (see Expenses changes) |
| Coordinator / Finance reject | Draft | Stays `Claimed` by the same report; the member fixes and resubmits (idempotent claim, fresh stamp) |
| Withdraw from Submitted / CoordinatorEndorsed | Withdrawn (terminal) | `Claimed → Open` (`ReleaseAsync`). `ExpenseReportId` kept for the trail. The member files a **new** report against the reopened grant; the withdrawn one is dead, as in Expenses today |
| Approve | Approved | `Claimed → Consumed` (`ConsumeAsync`), same transaction as the Holded push enqueue |
| Withdraw from Approved | Withdrawn | Grant stays `Consumed`. The Holded documents were booked and Expenses retains them; as far as the books know the money was authorized and spent. Nothing reopens |

## Program ceiling

When `TotalCeiling` is set: **Σ `MaxAmount` over grants in `Open` + `Claimed` + `Consumed` ≤
`TotalCeiling`.** Enforced at approve/issue (refused with the remaining headroom in the message)
and at a ceiling edit (refused when the new ceiling is below what is already committed). `Applied`
rows do not count; a manager sees "requested" beside "committed" so they can see the queue would
blow the ceiling before approving.

The program page always shows, from the grants as they stand: **granted** (Open + Claimed +
Consumed), **consumed**, **not claimed** (Revoked, plus `Open` past `ClaimBy`), **requested** (Applied) and the
ceiling. These are sums over frozen rows, never stored and never adjusted when a program closes.

## Public surface (`Humans.Preapprovals.Contracts`)

Narrowest contract that serves Expenses, per the hard rules.

```csharp
public interface IPreapprovalServiceRead
{
    // Grants the member may file against today: Open and ClaimBy not passed.
    Task<IReadOnlyList<GrantOption>> ListClaimableForUserAsync(Guid userId, CancellationToken ct);
    Task<GrantOption?> GetGrantAsync(Guid grantId, CancellationToken ct);
}

public sealed record GrantOption(
    Guid Id, string ProgramName, string? Label, decimal MaxAmount,
    int HoldedAccountNumber, string HoldedAccountId, LocalDate ClaimBy, GrantStatus Status);

public interface IPreapprovalService : IApplicationService, IPreapprovalServiceRead
{
    // Called by Expenses only. Each one audited with the report id.
    Task<GrantStamp> ClaimAsync(Guid grantId, Guid expenseReportId, Guid actorUserId, CancellationToken ct); // Open → Claimed; idempotent for the same report; throws unless claimable
    Task ReleaseAsync(Guid grantId, Guid expenseReportId, Guid actorUserId, CancellationToken ct); // Claimed → Open (withdraw before approval)
    Task ConsumeAsync(Guid grantId, Guid expenseReportId, Guid actorUserId, CancellationToken ct); // Claimed → Consumed (approve)
}
```

Program and manager administration, issuing, applying, approving, declining and revoking live on
an `internal interface IPreapprovalManagementService : IPreapprovalService` inside the section
(the `ITeamManagementService : ITeamService` pattern), which the section's own controllers
inject. Nothing outside the section sees them. `.Contracts` is a leaf project because Expenses →
Preapprovals and (later) Creativity → Preapprovals must not pull the section itself; when
Creativity exists it gets one narrow issue command on the public contract, not the whole
management surface.

`ClaimAsync` returns the stamp Expenses must write: `GrantStamp(decimal MaxAmount,
int HoldedAccountNumber, string HoldedAccountId, LocalDate ClaimBy)`, read from the program as
it is **at submit time**.

## Expenses changes

Builds on Phase 2's `ExpenseReport.HoldedAccountNumber` / nullable category.

- `ExpenseReport.PreapprovalGrantId` (Guid?, bare).
- **Expenses index (`/Expenses`):** when the member has any claimable grant, an "Open
  pre-approvals" block sits **above** the reports list: program, label, amount, **claim by** (danger
  style inside 7 days), and a "File expense" button per grant that opens
  `/Expenses/New?grant={id}` with the grant pre-selected. No grants, no block.
- **New report:** the New form lists the member's claimable grants above the account/category
  picker ("File against a pre-approval"), pre-selected when `?grant=` is given. Picking one sets `PreapprovalGrantId` and shows the
  grant's cap and account read-only. The draft stores the program's account at that moment
  (`HoldedAccountNumber` is non-nullable after Phase 2) and the grant's cap for display; submit
  overwrites both from the stamp. A finance admin
  filing on a member's behalf may pick one of **that member's** grants.
- **Submit:** `ClaimAsync` in the same transaction as the status change; its `GrantStamp` is
  written to `MaxAmount`, `HoldedAccountNumber` / `HoldedAccountId` **then**, so a program whose
  account was changed between draft and submit books to the account the program has now. If the
  grant is no longer claimable (revoked, claimed by another report, `ClaimBy` passed)
  submit is refused with a message naming why. Submitting on `ClaimBy` itself is allowed; the
  day after is not. **The deadline gates the first submit only.** A report that already holds
  the grant (rejected, back in `Draft`) may resubmit after `ClaimBy`: it was filed in time and
  the rejection is Finance's to finish, not a second filing. It gets a fresh stamp.
- **Endorsement skipped:** a pre-approved report goes `Submitted → Finance review`. The grant is
  the endorsement; the program's managers already decided. (Matches Phase 2's rule for unmapped
  accounts.)
- **Approve:** Finance may lower the cap, never raise it above the grant's **current** `MaxAmount`
  (re-read at approve, so a grant raised after submit lets Finance pay the new figure). A blank
  cap field on a pre-approved report means the grant's current `MaxAmount`, never "no cap":
  the cap on such a report is never null. The
  account is not overridable on a pre-approved report (the program chose it). `ConsumeAsync`
  in the same transaction as the Holded push enqueue.
- **Reject (either decider):** nothing on the grant; the report is back in `Draft` and still
  holds it. **Withdraw before approval:** `ReleaseAsync`; the grant reopens and the member files
  a new report (a withdrawn report is terminal in Expenses). **Withdraw after approval:** nothing
  on the grant; it stays `Consumed`. The table under the state machine is the full matrix.
- **Detail view:** a "Pre-approved: {ProgramName} · {Label} · up to {MaxAmount} · claim by
  {ClaimBy}" banner for everyone who can see the report.
- **Deadline on screen:** the New form, the Draft edit page and the detail view show `ClaimBy`
  for a pre-approved report in the page's alert style, with days remaining, and switch to the
  danger style inside the last 7 days. The member cannot miss it. A rejected draft that
  already holds the grant shows "filed in time, resubmit when ready" instead of the countdown.
- `Payable`, `PayableAllocation`, the push and the creditor ledger are untouched.

## Preapprovals UI

### Member side (localized, six cultures)

| Route | Who | What |
|-------|-----|------|
| `/Preapprovals` | Authenticated | My grants, all statuses: program, label, amount, status, **claim by** (prominent, danger style inside 7 days, "deadline passed" after). A row links to `/Expenses/New?grant={id}` only while it is **claimable** (`Open` and `ClaimBy` not passed); a "deadline passed" row has no link. The open ones also appear at the top of `/Expenses`, which is where most people will file from. Programs accepting applications are listed below with an Apply button. |
| `/Preapprovals/{programId}` | Authenticated | Program page: description, per-person amount or "amount on request", claim deadline, "Apply for a pre-approval" form when `Status == Open` **and** `AcceptsApplications` **and** `ClaimBy` not passed (note; requested amount on `PerGrantAmount` programs). The GET hides the form and the POST returns 400 when any of the three is false. One application per person per program; a second submit edits the pending one. |

### Manager side (`/Preapprovals/Admin/*`, its own `PreapprovalsAdminController`, localization-exempt, per `no-admin-url-section`)

| Route | Who | What |
|-------|-----|------|
| `/Preapprovals/Admin` | Board, FinanceAdmin, Admin, or a manager of ≥1 program | Programs I manage: committed / ceiling / remaining, open applications count, claim deadline. |
| `/Preapprovals/Admin/{id}` | Program manager or above | Grants table with status filter. Approve / decline (with amount on `PerGrantAmount`) / revoke. **Issue grant:** people picker (Users search, same control the Workgroups roster uses) + amount where applicable + label. **Issue many** (`FixedPerPerson` only): the same `<vc:human-search>` picker adds people one at a time to a pending list on the page (name, remove button), then one submit issues a grant at `PerPersonAmount` to each; all or nothing. No free-text name or email matching, per `person-search` ("don't roll a third"). |
| `/Preapprovals/Admin/New`, `/Preapprovals/Admin/{id}/Edit` | Board, FinanceAdmin, Admin | Create / edit program: name, description, kind, per-person amount, ceiling, account picker (`ListExpenseAccountsAsync`), accepts applications, claim-by, status, managers (people picker). |

### Navigation

- Member: a "Pre-approvals" entry beside "Expenses" in the member menu, shown when the member
  has any grant or any program accepts applications.
- Manager: "Manage pre-approvals" under the same menu for anyone who passes the manage policy.
- Expenses' index block and New form link back to `/Preapprovals` ("All my pre-approvals").

## Authorization

| Actor | Can |
|-------|-----|
| Member | See own grants; apply to programs accepting applications; file an expense against own `Open` grant. |
| Program manager | Everything a member can. In **their** programs: issue, issue-many, approve, decline, revoke; view the grants table. |
| Board, FinanceAdmin, Admin | Everything a manager can, in every program. Create and edit programs, set ceiling, account, deadline, managers; close a program. |

Negative cases to verify:

- A member cannot see another member's grants, cannot apply twice, cannot apply to a `Closed`
  program or one not accepting applications, cannot file against a
  `Claimed`/`Revoked`/`Declined` grant, a grant past `ClaimBy`, or another person's grant (403 before mutation).
- A manager of program A gets 403 on program B's admin routes and on `Admin/New` / `Edit`.
- A manager cannot change a program's amount, ceiling, account, deadline or managers.
- A submitter cannot change the stamped cap or account on a pre-approved report (no input, and
  the service ignores posted values).
- Finance cannot raise the cap above the grant or change the account at approve.
- Nobody can make the **first** submit of a pre-approved report after `ClaimBy`, admins on behalf included. A rejected report that already holds the grant may resubmit (see Expenses changes).

## Notifications and email

Through `INotificationEmitter` (in-app) and `IEmailService` (email), both to the beneficiary.
Every message below is member-facing: resx in all six cultures, rendered in the recipient's
preferred culture (the English here is the key's meaning, not the copy):

| Event | Priority | Content |
|-------|----------|---------|
| Grant issued / application approved | Actionable | Program, amount, **claim by {date}**, link to file. |
| Application declined | Informational | Program, note from the manager if any. |
| Application received (to each manager of the program) | Actionable | Who, requested amount, link to the manage page. |
| Deadline reminder, 14 days and 3 days before `ClaimBy`, to every `Open` grant holder | Actionable | "File by {date}." Sent by the nightly job; each send is recorded in `pre_reminders_sent` so a retry never sends it twice. |
| Report rejected (Expenses' existing rejection notification) | Actionable | For a pre-approved report it also says the report was filed in time and may be fixed and resubmitted; no deadline is imposed, since the holding report is exempt from `ClaimBy` (see Expenses changes). The grant is `Claimed` during a rejection, so the deadline job does not reach it. |
| Grant amount changed | Informational | Program, old and new amount. |
| Grant revoked | Actionable | Program, label, amount, reason if given. For a `Claimed` grant it links the report and says what to do (drop or swap the grant on the draft, or wait for Finance to reject a submitted one). The report's detail view and Finance's review row show "pre-approval revoked" from the moment of revocation, not first at approve. |

Two source keys so `INotificationAutoResolve` clears the right alert: `pre-grant:{id}` on the
beneficiary's "file by" notification, resolved when the grant leaves `Open`; `pre-request:{id}`
on the managers' "application received" notification, resolved on approve or decline.

## Audit

Every state change on a program or a grant writes an audit entry with the actor, the grant /
program id, and the amounts: `PreapprovalProgramCreated`, `…Edited` (old and new ceiling /
amount / deadline in the message), `…Closed`, `…ManagerAdded`, `…ManagerRemoved`,
`PreapprovalGrantIssued`, `…Applied`, `…Approved`, `…Declined`, `…Revoked`, `…Claimed`,
`…Released`, `…Consumed`, `PreapprovalGrantAmountChanged` (old and new). The Expenses-triggered ones carry the report id.

## GDPR

Personal data added: the beneficiary / applicant user id, `ApplicationNote`, `RequestedAmount`,
`Label`, and the actor ids `CreatedByUserId`, `AddedByUserId`, `DecidedByUserId`.

**Export** (`IUserDataContributor`): two slices. `Preapprovals` is the person's own grants
(program name, label, amounts, status, dates, note). `PreapprovalDecisions` is what they did as
an actor: programs created, managers added, grants issued / approved / declined / revoked, by
program and date.

**Erasure** follows Expenses. A grant is the authorization behind an accounting voucher and the
Board must be able to see what was granted and what happened to it, so `ErasureDeclaration`
maps `Preapprovals` and `PreapprovalDecisions` to the same fiscal-retention reason as
`ExpenseReports`: every grant that was ever decided (`Open`, `Claimed`, `Consumed`, `Declined`,
`Revoked`) is retained in full, beneficiary and actor ids included, exactly as an
expense report's submitter and approver are. `EraseForUserAsync` deletes only what was never
decided: the person's `Applied` rows, and their `pre_program_managers` rows. A `Claimed` grant
is never released or deleted by erasure; its report is retained by Expenses and finishes
normally.

**Merge** (`IUserMerge`): `pre_grants.BeneficiaryUserId`, `pre_program_managers.UserId` and the
actor columns are re-keyed from the eliminated id to the survivor. A survivor already managing
the same program keeps one row. Two `Applied` rows for the same program collapse to the older
one (the other is deleted, audited as `…MergeDroppedApplication`); decided grants are never
merged, a person can legitimately hold two. Until the merge has run, reads go through `UserInfo.AllUserIds`
like Expenses' creditor binding does, so a grant issued under a since-merged id is still found.

## Background job

`PreapprovalsSectionJobs`: one nightly job, `preapprovals-reminders`, which **sends** the
14-day and 3-day reminders to `Open` grant holders and records each send in
`pre_reminders_sent` (GrantId, Kind `D14` | `D3`, ClaimBy, SentAt; PK GrantId + Kind + ClaimBy). The in-app
notification, the email outbox row, the marker and one audit entry
(`PreapprovalReminderSent`, system actor, program, grant, kind, deadline) are written in one
ambient `TransactionScope` per grant (all four contexts share the database), so a crash leaves
either all of them or none and every committed send is visible to the Board. That delivery log
and those audit entries are the only rows the job writes; neither is grant state. No job expires, closes, zeroes or otherwise touches a grant
or program; every state change is a person's action and is audited as such.
Documented in [background-jobs.md](background-jobs.md) when built.

## Out of scope

- Partial draw-down of one grant across several reports. One grant, one report. A creativity
  lead who needs two reports gets two grants.
- Spent-vs-granted from the Holded ledger on the program page. Later pull via
  `IHoldedFinanceServiceRead.GetCreditorLedgerAsync`, same posture as Workgroups.
- A Creativity section. When it exists it gets one narrow command on the public contract
  (`IssueAsync(programId, beneficiaryUserId, amount, label)`, added then, not now); it never
  sees the management interface.
- Linking a program to a Workgroup or a Team. A program is its own thing; the label and the
  account say what it is for.
- Per-grant deadline extensions. Everyone in a program has the same `ClaimBy`; moving it moves
  it for all of them.

## Delivery order

1. **workgroup-budget Phase 2** in Expenses (account on the report, nullable category). Own PR.
2. **Preapprovals section**: DbContext + migration, entities, service, repository, contracts
   project, manager/member UI, notifications, audit, GDPR contributor, reminder job, `Docs/Preapprovals.md`
   per `SECTION-TEMPLATE.md`, `tests/Humans.Preapprovals.Tests`. Own PR. Usable standalone: a
   manager can already issue the 50 offsite grants and members can see their deadline.
3. **Expenses hook**: `PreapprovalGrantId`, New-form grant picker, claim/release/consume calls,
   endorsement skip, cap/account lock, deadline banners, `Expenses.md` update, tests. Own PR.

Steps 2 and 3 can be built in parallel once Phase 2 is on QA; 3 merges after 2.

## Tests (what each PR must prove)

- **Preapprovals**: ceiling refused at issue/approve/edit with the right headroom; `FixedPerPerson`
  refuses an amount on the grant and copies `PerPersonAmount`; zero or negative
  `PerPersonAmount`, `MaxAmount` and `RequestedAmount` refused on every path; revoke notifies the
  holder; apply-twice edits, never
  duplicates; claim/release/consume transitions and every refused transition;
  reminders idempotent through `pre_reminders_sent` per deadline and write nothing else; "deadline passed"
  rows carry no filing link; issue-many is all or nothing; manager of A → 403 on B; member → 403 on admin routes;
  closed program: apply form hidden, POST refused, `Open` grants still claimable; `Open` grant
  past `ClaimBy` not claimable, claimable again after `ClaimBy` is pushed back with no state
  change; amount edit on `Open`/`Claimed` audited with old and new and ceiling-checked, refused
  on terminal states; revoke / close leave `MaxAmount` untouched; claim idempotent for
  the holding report and refused for another; resubmit of the holding report allowed after
  `ClaimBy`; revoke on `Claimed` refuses the next claim/consume and the draft can drop or swap
  the grant; `ClaimBy` edit resolves the old notification, notifies holders and reminders run again for the new date; apply refused after `ClaimBy`;
  each reminder send commits its audit entry with its marker; merge collapses duplicate
  `Applied` rows; export (both slices), erasure keeps decided grants
  and deletes `Applied` + manager rows; merge re-keys and dedups manager rows; every state change
  audited.
- **Expenses**: draft stores the program's account at creation; stamp written at submit from
  the program's current account, overwriting it; resubmit after reject restamps; submit after `ClaimBy` refused;
  submit on a non-claimable grant refused; endorsement skipped; approve cannot raise the cap above the grant's current amount or
  change the account, and a blank cap keeps the grant's current amount; reject leaves the grant `Claimed`; withdraw before approval releases;
  withdraw after approval leaves it `Consumed`; approve consumes in the same transaction as the
  push enqueue; on-behalf filing restricted to the member's own grants.

## Related

- [workgroup-budget.md](workgroup-budget.md) — Phase 2 is the prerequisite.
- `src/Sections/Humans.Expenses/Docs/Expenses.md` — cap, payable, allocation invariants.
- `src/Sections/Humans.Finance/Docs/Finance.md` — account registry.
- [background-jobs.md](background-jobs.md), [gdpr-export.md](gdpr-export.md).
