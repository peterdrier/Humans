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
| Claim deadline | The program's `ClaimBy` date. Expense reports against its grants must be **submitted** by then. Prominent everywhere; no grace. |

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

## Data model (`PreapprovalsDbContext`, all tables owned by `Humans.Preapprovals`)

### `preapproval_programs`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| Name | string(200) | "2026 Creativity" |
| Description | string(2000)? | Shown on the public program page and the apply form |
| Kind | enum `ProgramKind` | `FixedPerPerson` (every grant is `PerPersonAmount`) / `PerGrantAmount` (amount set on each grant) |
| PerPersonAmount | decimal(18,2)? | Required when `FixedPerPerson`; refused otherwise |
| TotalCeiling | decimal(18,2)? | Null = no ceiling. Creativity: 25 000 → 50 000 → 60 000, edited in place; each edit audited so the history is in the log |
| HoldedAccountNumber | int | From `IHoldedFinanceServiceRead.ListExpenseAccountsAsync(activeOnly: true)` |
| HoldedAccountId | string(64) | |
| AcceptsApplications | bool | Public apply page open |
| ClaimBy | LocalDate | Claim deadline. Required. Reports must be submitted on or before this date (Europe/Madrid, end of day) |
| Status | enum `ProgramStatus` | `Open` / `Closed`. Closed: no new grants, no applications; existing grants still usable until `ClaimBy` |
| CreatedByUserId, CreatedAt, UpdatedAt | | |

### `preapproval_program_managers`

| Property | Type | Notes |
|----------|------|-------|
| ProgramId | Guid | FK → `preapproval_programs` |
| UserId | Guid | Bare Guid (Users), no FK |
| AddedByUserId, AddedAt | | |

PK `(ProgramId, UserId)`. One role; a manager is a manager.

### `preapproval_grants`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| ProgramId | Guid | FK |
| BeneficiaryUserId | Guid | Bare Guid |
| Label | string(200)? | The art project name; null for travel |
| MaxAmount | decimal(18,2) | Copied from `PerPersonAmount` for `FixedPerPerson`; entered for `PerGrantAmount` |
| Status | enum `GrantStatus` | `Applied` / `Open` / `Claimed` / `Consumed` / `Declined` / `Revoked` / `Expired` |
| ApplicationNote | string(2000)? | What the applicant wrote; null when issued directly |
| RequestedAmount | decimal(18,2)? | What the applicant asked for (`PerGrantAmount` programs); the manager may approve less |
| DecidedByUserId, DecidedAt | | Who approved / declined / issued |
| ExpenseReportId | Guid? | Bare Guid (Expenses). Set at `Claimed`, kept at `Consumed` |
| CreatedAt, UpdatedAt | | |

A grant and an application are the same row: an application is a grant in `Applied`.

## Grant state machine

```
Applied ──approve──▶ Open ──report submitted──▶ Claimed ──report approved──▶ Consumed
   │                  │                            │
   └──decline──▶ Declined                          └──report withdrawn / rejected──▶ Open
                      │
                      └──revoke──▶ Revoked        Open ──ClaimBy passed, no report──▶ Expired
```

- `Open` is the only state a member can file against.
- `Claimed` holds the grant while the report is in flight so it cannot be used twice. Withdraw
  or reject (either decider) reopens it. One grant, one report at a time.
- `Consumed` is terminal. So are `Declined`, `Revoked`, `Expired`.
- `Expired` is set by the nightly job for `Open` grants whose program's `ClaimBy` has passed
  (`ISectionJobs`, Preapprovals' own). A `Claimed` grant is never expired by the job: the report
  was submitted in time and is Finance's to finish.
- Revoke is allowed on `Open` only. A `Claimed` grant is revoked by rejecting its report.

## Program ceiling

When `TotalCeiling` is set: **Σ `MaxAmount` over grants in `Open` + `Claimed` + `Consumed` ≤
`TotalCeiling`.** Enforced at approve/issue (refused with the remaining headroom in the message)
and at a ceiling edit (refused when the new ceiling is below what is already committed). `Applied`
rows do not count; a manager sees "requested" beside "committed" so they can see the queue would
blow the ceiling before approving.

## Public surface (`Humans.Preapprovals.Contracts`)

Narrowest contract that serves Expenses, per the hard rules.

```csharp
public interface IPreapprovalServiceRead
{
    // Grants the member may file against today: Open, program Open, ClaimBy not passed.
    Task<IReadOnlyList<GrantOption>> ListClaimableForUserAsync(Guid userId, CancellationToken ct);
    Task<GrantOption?> GetGrantAsync(Guid grantId, CancellationToken ct);
}

public sealed record GrantOption(
    Guid Id, string ProgramName, string? Label, decimal MaxAmount,
    int HoldedAccountNumber, string HoldedAccountId, LocalDate ClaimBy);

public interface IPreapprovalService : IApplicationService, IPreapprovalServiceRead
{
    // Called by Expenses only. Each one audited with the report id.
    Task ClaimAsync(Guid grantId, Guid expenseReportId, Guid actorUserId, CancellationToken ct);   // Open → Claimed; throws unless Open and claimable
    Task ReleaseAsync(Guid grantId, Guid expenseReportId, Guid actorUserId, CancellationToken ct); // Claimed → Open (withdraw / reject)
    Task ConsumeAsync(Guid grantId, Guid expenseReportId, Guid actorUserId, CancellationToken ct); // Claimed → Consumed (approve)
}
```

Program and manager administration, issuing, applying, approving, declining and revoking are
section-internal service methods on the same `IPreapprovalService` and are not listed here
because no other section calls them. `.Contracts` is a leaf project because Expenses →
Preapprovals and (later) Creativity → Preapprovals must not pull the section itself.

## Expenses changes

Builds on Phase 2's `ExpenseReport.HoldedAccountNumber` / nullable category.

- `ExpenseReport.PreapprovalGrantId` (Guid?, bare).
- **New report:** the New form lists the member's claimable grants above the account/category
  picker ("File against a pre-approval"). Picking one stamps `PreapprovalGrantId`, `MaxAmount`,
  `HoldedAccountNumber` / `HoldedAccountId`, and locks all three for the submitter. A finance
  admin filing on a member's behalf may pick one of **that member's** grants.
- **Submit:** `ClaimAsync` in the same transaction as the status change. If the grant is no
  longer claimable (revoked, expired, claimed by another draft, `ClaimBy` passed) submit is
  refused with a message naming why. Submitting on `ClaimBy` itself is allowed; the day after is
  not.
- **Endorsement skipped:** a pre-approved report goes `Submitted → Finance review`. The grant is
  the endorsement; the program's managers already decided. (Matches Phase 2's rule for unmapped
  accounts.)
- **Approve:** Finance may lower the cap, never raise it above the grant's `MaxAmount`; the
  account is not overridable on a pre-approved report (the program chose it). `ConsumeAsync`
  in the same transaction as the Holded push enqueue.
- **Withdraw / reject (either decider):** `ReleaseAsync`. The report keeps its stamped cap and
  account on resubmit; the grant reopens. A withdrawn-then-resubmitted report after `ClaimBy` is
  refused at submit like any other.
- **Detail view:** a "Pre-approved: {ProgramName} · {Label} · up to {MaxAmount} · claim by
  {ClaimBy}" banner for everyone who can see the report.
- **Deadline on screen:** the New form, the Draft edit page and the detail view show `ClaimBy`
  for a pre-approved report in the page's alert style, with days remaining, and switch to the
  danger style inside the last 7 days. The member cannot miss it.
- `Payable`, `PayableAllocation`, the push and the creditor ledger are untouched.

## Preapprovals UI

### Member side (localized, six cultures)

| Route | Who | What |
|-------|-----|------|
| `/Preapprovals` | Authenticated | My grants: program, label, amount, status, **claim by** (prominent, danger style inside 7 days, "expired" after). Each `Open` row links to `/Expenses/New?grant={id}`. Programs accepting applications are listed below with an Apply button. |
| `/Preapprovals/{programId}` | Authenticated | Program page: description, per-person amount or "amount on request", claim deadline, Apply form when `AcceptsApplications` (note; requested amount on `PerGrantAmount` programs). One application per person per program; a second submit edits the pending one. |

### Manager side (`/Preapprovals/Manage/*`, admin-exempt from localization, per `no-admin-url-section`)

| Route | Who | What |
|-------|-----|------|
| `/Preapprovals/Manage` | Board, FinanceAdmin, Admin, or a manager of ≥1 program | Programs I manage: committed / ceiling / remaining, open applications count, claim deadline. |
| `/Preapprovals/Manage/{id}` | Program manager or above | Grants table with status filter. Approve / decline (with amount on `PerGrantAmount`) / revoke. **Issue grant:** people picker (Users search, same control the Workgroups roster uses) + amount where applicable + label. **Issue many:** paste a list of emails or names, one grant each at `PerPersonAmount` (`FixedPerPerson` only); unresolved rows are reported, nothing partial. |
| `/Preapprovals/Manage/New`, `/Preapprovals/Manage/{id}/Edit` | Board, FinanceAdmin, Admin | Create / edit program: name, description, kind, per-person amount, ceiling, account picker (`ListExpenseAccountsAsync`), accepts applications, claim-by, status, managers (people picker). |

### Navigation

- Member: a "Pre-approvals" entry beside "Expenses" in the member menu, shown when the member
  has any grant or any program accepts applications.
- Manager: "Manage pre-approvals" under the same menu for anyone who passes the manage policy.
- Expenses' New form links back to `/Preapprovals` ("See my pre-approvals").

## Authorization

| Actor | Can |
|-------|-----|
| Member | See own grants; apply to programs accepting applications; file an expense against own `Open` grant. |
| Program manager | Everything a member can. In **their** programs: issue, issue-many, approve, decline, revoke; view the grants table. |
| Board, FinanceAdmin, Admin | Everything a manager can, in every program. Create and edit programs, set ceiling, account, deadline, managers; close a program. |

Negative cases to verify:

- A member cannot see another member's grants, cannot apply twice, cannot file against a
  `Claimed`/`Revoked`/`Expired`/`Declined` grant or another person's grant (403 before mutation).
- A manager of program A gets 403 on program B's manage routes and on `Manage/New` / `Edit`.
- A manager cannot change a program's amount, ceiling, account, deadline or managers.
- A submitter cannot change the stamped cap or account on a pre-approved report (no input, and
  the service ignores posted values).
- Finance cannot raise the cap above the grant or change the account at approve.
- Nobody can submit a pre-approved report after `ClaimBy`, admins on behalf included.

## Notifications and email

Through `INotificationEmitter` (in-app) and `IEmailService` (email), both to the beneficiary:

| Event | Priority | Content |
|-------|----------|---------|
| Grant issued / application approved | Actionable | Program, amount, **claim by {date}**, link to file. |
| Application declined | Informational | Program, note from the manager if any. |
| Application received (to each manager of the program) | Actionable | Who, requested amount, link to the manage page. |
| Deadline reminder, 14 days and 3 days before `ClaimBy`, to every `Open` grant holder | Actionable | "File by {date} or the pre-approval lapses." Nightly job, idempotent per grant per reminder. |
| Expired | Informational | "Your pre-approval under {program} lapsed on {date}." |

Notification source key `preapproval-grant:{id}` so `INotificationAutoResolve` clears the
"file by" notification when the grant leaves `Open`.

## Audit

Every state change on a program or a grant writes an audit entry with the actor, the grant /
program id, and the amounts: `PreapprovalProgramCreated`, `…Edited` (old and new ceiling /
amount / deadline in the message), `…Closed`, `…ManagerAdded`, `…ManagerRemoved`,
`PreapprovalGrantIssued`, `…Applied`, `…Approved`, `…Declined`, `…Revoked`, `…Claimed`,
`…Released`, `…Consumed`, `…Expired`. The Expenses-triggered ones carry the report id.

## GDPR

Personal data added: the beneficiary / applicant user id, `ApplicationNote`, `RequestedAmount`,
`Label`. `IUserDataContributor` for Preapprovals exports a person's grants (program name, label,
amount, status, dates, note). Erasure follows Expenses: a `Consumed` grant is part of the
accounting voucher trail and is retained (`ErasureDeclaration`, same fiscal-retention reason as
`ExpenseReports`). Every other grant (`Applied`, `Open`, `Declined`, `Revoked`, `Expired`) is
deleted on erasure; a `Claimed` grant is released first (its report is being erased or retained
by Expenses' own rule). Manager rows are deleted on erasure.

## Background job

`PreapprovalsSectionJobs`: one nightly job, `preapprovals-deadlines`. Expires `Open` grants whose
program's `ClaimBy` has passed (Europe/Madrid), sends the 14-day and 3-day reminders, writes the
`…Expired` audit entries. Documented in [background-jobs.md](background-jobs.md) when built.

## Out of scope

- Partial draw-down of one grant across several reports. One grant, one report. A creativity
  lead who needs two reports gets two grants.
- Spent-vs-granted from the Holded ledger on the program page. Later pull via
  `IHoldedFinanceServiceRead.GetCreditorLedgerAsync`, same posture as Workgroups.
- A Creativity section. When it exists it calls the section-internal issue path with the
  project lead, amount and label; nothing here changes for it.
- Linking a program to a Workgroup or a Team. A program is its own thing; the label and the
  account say what it is for.
- Changing `ClaimBy` after grants exist is allowed (Board / FinanceAdmin, audited) but there is
  no per-grant extension. Everyone in a program has the same deadline.

## Delivery order

1. **workgroup-budget Phase 2** in Expenses (account on the report, nullable category). Own PR.
2. **Preapprovals section**: DbContext + migration, entities, service, repository, contracts
   project, manager/member UI, notifications, audit, GDPR contributor, nightly job, `Docs/Preapprovals.md`
   per `SECTION-TEMPLATE.md`, `tests/Humans.Preapprovals.Tests`. Own PR. Usable standalone: a
   manager can already issue the 50 offsite grants and members can see their deadline.
3. **Expenses hook**: `PreapprovalGrantId`, New-form grant picker, claim/release/consume calls,
   endorsement skip, cap/account lock, deadline banners, `Expenses.md` update, tests. Own PR.

Steps 2 and 3 can be built in parallel once Phase 2 is on QA; 3 merges after 2.

## Tests (what each PR must prove)

- **Preapprovals**: ceiling refused at issue/approve/edit with the right headroom; `FixedPerPerson`
  refuses an amount on the grant and copies `PerPersonAmount`; apply-twice edits, never
  duplicates; claim/release/consume transitions and every refused transition; expiry job skips
  `Claimed`; reminders idempotent; manager of A → 403 on B; member → 403 on manage routes;
  export and erasure paths; every state change audited.
- **Expenses**: picking a grant stamps and locks cap and account; submit after `ClaimBy` refused;
  submit on a non-claimable grant refused; endorsement skipped; approve cannot raise the cap or
  change the account; withdraw/reject releases; approve consumes in the same transaction as the
  push enqueue; on-behalf filing restricted to the member's own grants.

## Related

- [workgroup-budget.md](workgroup-budget.md) — Phase 2 is the prerequisite.
- `src/Sections/Humans.Expenses/Docs/Expenses.md` — cap, payable, allocation invariants.
- `src/Sections/Humans.Finance/Docs/Finance.md` — account registry.
- [background-jobs.md](background-jobs.md), [gdpr-export.md](gdpr-export.md).
