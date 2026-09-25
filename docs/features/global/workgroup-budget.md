<!-- freshness:triggers
  src/Sections/Humans.Workgroups/**
  src/Sections/Humans.Finance/Services/Service.cs
  src/Sections/Humans.Finance.Contracts/**
  src/Sections/Humans.Expenses/Services/ExpenseReportService.cs
-->
<!-- freshness:flag-on-change
  Which section owns the budget amount vs the Holded account registry, the account naming and
  dedup rules, and the "unmapped accounts skip endorsement" decision — review when Workgroups'
  budget fields, Finance's managed-account registry, or Expenses' spend-target model change.
-->

# Workgroup budgets — a Holded account per budgeted workgroup

Some workgroups (~10%) are allocated money. The allocation is not part of the event budget
(no `BudgetYear` / `BudgetCategory`); it is a number on the register plus a dedicated Holded
P&L expense account the group's spending is booked to. Recurring subjects are a new workgroup
per year ("ALM 2027"), so each year gets its own account.

Design settled with Peter, 2026-09-25.

## Ownership

| Fact | Owner | Why |
|------|-------|-----|
| Budget amount | Workgroups | A register attribute shown to people browsing the group. Finance and Expenses never read it. |
| Which Holded account the group books to | Workgroups (`HoldedAccountNumber` / `HoldedAccountId` on the row) | Same posture as `DriveFolderId`: an opaque external id, no FK. |
| Holded account creation, naming, numbering, dedup | Finance | Finance already provisions expense accounts for budget categories; the collision rules live once. |
| Registry of expense accounts Finance created outside the budget map | Finance (`holded_managed_accounts`) | Finance's own concern: the doc sync must not dump those accounts' docs on the Unmatched queue, and Expenses needs an "active" flag. Finance stores no workgroup id. |
| Expense report → Holded account | Expenses (phase 2) | A report knows which account it goes to, not which budget it belongs to. |

Nobody outside Workgroups learns what a workgroup is. The later "ledger for this account" view on
the group page is a pull by Workgroups through `IHoldedService.GetLedgerLinesAsync(accountNum)`.

## Phase 1 — Workgroups + Finance

### Finance

**`holded_managed_accounts`** (new table, `FinanceDbContext`)

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| HoldedAccountNumber | int | Unique |
| HoldedAccountId | string(64) | Holded's id; unique |
| Label | string(200) | The name the account was created or linked with |
| IsActive | bool | Retired accounts drop out of `ListExpenseAccountsAsync(activeOnly: true)` |
| CreatedAt / UpdatedAt | Instant | |

**Public surface** (`Humans.Finance.Contracts`) — new members, approved by Peter:

- `IHoldedFinanceService.CreateOrLinkExpenseAccountAsync(string name, int? existingAccountNum, CancellationToken)`
  → `HoldedExpenseAccountRef(int AccountNum, string AccountId, string Name, bool Created)`.
  1. `existingAccountNum` given: the account must exist in the live chart
     (`IHoldedClient.ListExpenseAccountsAsync`); returns it, `Created = false`. Unknown number → throws.
  2. Otherwise match `name` against the live chart by normalized name (trim, collapse whitespace,
     case-insensitive, accents folded). A hit is returned, `Created = false`. This is the
     dup guard: two clicks, or a name typed twice, never produce twin accounts.
  3. Otherwise allocate the next free number in the `62900100` block against the local
     category map, the local managed registry and the live chart (same loop as
     `GetProvisioningPlanAsync`), create the account, return it with `Created = true`.
  Every path upserts a `holded_managed_accounts` row unless the account is already in
  `holded_category_map` (a budget-category account linked on purpose stays Finance's category
  account and is not re-registered). Audited (`HoldedExpenseAccountCreated` / `…Linked`).
- `IHoldedFinanceService.SetExpenseAccountActiveAsync(int accountNum, bool isActive, CancellationToken)`
  — flips the registry row. No-op when the number is a category account or unknown.
- `IHoldedFinanceServiceRead.ListExpenseAccountsAsync(bool activeOnly, CancellationToken)`
  → `IReadOnlyList<HoldedExpenseAccountOption(int AccountNum, string AccountId, string Label, bool IsBudgetCategory, bool IsActive)>`.
  Union of the category map (label `Group / Category` from the active budget year, always active)
  and the managed registry (its `Label`). Cache reads only — no Holded call. This is what the
  Workgroups picker and the phase-2 Expenses dropdown consume; the "link existing" picker in
  Workgroups shows the full live chart via `IHoldedClient.ListExpenseAccountsAsync` so an account
  created by hand in Holded can be linked too.

**Doc sync.** `HoldedMatcher.Match` gets the managed registry's account ids alongside the category
map entries. A doc booked to a managed account is `MatchStatus = Matched`, `MatchSource = Account`,
`BudgetCategoryId = null`. It stays off `/Finance/HoldedUnmatched` and off the budget-year actuals
(`GetActualsForYearAsync` already filters on a non-null category). `/Finance/Holded` lists the
managed registry beside the category map.

### Workgroups

**Columns on `workgroups`** (one migration, `WorkgroupsDbContext`):

| Property | Type | Notes |
|----------|------|-------|
| BudgetAmount | decimal(18,2)? | Null = no budget. The UI checkbox reveals the field; there is no separate flag. |
| HoldedAccountNumber | int? | Set together with the id; null until an account is bound |
| HoldedAccountId | string(64)? | |

**Service.** `IWorkgroupService.SetBudgetAsync(Guid workgroupId, Guid actorUserId, WorkgroupBudgetSave save, CancellationToken)`
with `WorkgroupBudgetSave(decimal? Amount, int? ExistingAccountNum)` — a non-null `ExistingAccountNum` means "link that account".

- Amount null clears the budget; the account binding is kept (Holded accounts are never
  deleted, and a group that had money once keeps its ledger).
- Amount set and no account bound yet: call `CreateOrLinkExpenseAccountAsync` with
  `"Workgroups / {Name}"`, passing `ExistingAccountNum` when given. Store number and id.
- Amount set and an account already bound: amount-only change unless `ExistingAccountNum` names a
  different number, which rebinds. Rebinding never touches the old account.
- Amount must be ≥ 0 when set.
- Writes a system log entry (`WorkgroupLogKind.BudgetSet`, title carries the amount and
  account number) and an audit entry (`AuditAction.WorkgroupBudgetSet`, `relatedEntityId` = the
  workgroup). When Finance reports `Created = false` for a create request, the success message
  says the group was linked to the existing account of that name.
- Allowed on any status except Refused / Withdrawn.

**Lifecycle hooks.** `CloseAsync`, `MarkDoneAsync` (both → Dormant) and `WithdrawAsync` call
`SetExpenseAccountActiveAsync(number, false)` when an account is bound; `ReactivateAsync` calls
it with `true`. Finance failure on these calls is logged, not fatal — the lifecycle transition
is the Board's decision and must not hang on Holded.

**Authorization.** `BoardOrAdmin` only, on `WorkgroupsAdminController`
(`POST /Workgroups/Admin/{id}/Budget`). Members, including coordinators, cannot set or change
it. No `WorkgroupAdmin` role exists; adding one is a separate change.

**UI.**
- Group page: a "Budget" block showing the amount and the Holded account number/name. Rendered
  for Board/Admin and for viewers whose `MembershipTier` is Colaborador or Asociado; hidden from
  Volunteers. (Tier via `IUserServiceRead`, as the page already stitches for the roster.)
- Admin form (Board/Admin, on the group page beside the other admin controls): checkbox "This
  workgroup has a budget" → amount field → radio "Create `Workgroups / {Name}` in Holded"
  (default) / "Link an existing account" with a select of the live chart (number — name).
  The proposed name is shown before submit. When an account is already bound the form shows it
  and the radio defaults to keeping it.
- Register-existing bootstrap form (`/Workgroups/Admin/RegisterExisting`) gets the same fields.
- Localization: the member-visible budget block gets keys in all six cultures. The admin form
  under `/Workgroups/Admin/*` is localization-exempt.

**Docs.** `Workgroups.md` (data model, invariants, triggers, cross-section dependencies gain
`IHoldedFinanceService` and `IHoldedClient`), `authorization.md`, `data-access.md`; Finance's
`Finance.md` (new table, new surface, sync rule). Dependency graph edge Workgroups → Finance.

**GDPR.** No personal data added. Nothing to export or erase.

**Tests.**
- `Humans.Finance.Tests`: create path allocates past used numbers from map, registry and live
  chart; link-by-name normalization (case, whitespace, accents) returns the existing account and
  registers it; explicit number unknown → throws; category account linked → not re-registered;
  `SetExpenseAccountActiveAsync` no-op on category/unknown; sync marks a managed-account doc
  Matched with null category and keeps it out of Unmatched; `ListExpenseAccountsAsync(activeOnly)`.
- `Humans.Workgroups.Tests`: set budget creates the account with the `Workgroups / {Name}`
  label; second set is amount-only; clear keeps the binding; link-existing rebinds; negative
  amount rejected; Refused/Withdrawn rejected; log + audit written; Close/Done/Withdraw retire
  the account and Reactivate restores it; Finance failure on lifecycle is swallowed; member
  cannot call the admin route; Volunteer does not see the budget block, Colaborador does.

## Phase 2 — Expenses (separate PR, after phase 1 is on QA)

An expense report names the Holded account it goes to, not a budget category.

- `ExpenseReport.HoldedAccountNumber` (int) added; `BudgetCategoryId` and `BudgetYearId`
  become nullable and are derived at selection time for category-mapped accounts (account →
  category via Finance's map) so endorsement routing and year pinning keep working for
  department reports.
- The New/Edit dropdown is `IHoldedFinanceServiceRead.ListExpenseAccountsAsync(activeOnly: true)`,
  grouped "Budget categories" / "Other accounts".
- Reports on an unmapped account skip coordinator endorsement: Submitted → Finance review.
  Peter's decision; the Finance admin is the gate.
- Holded push books the line to `HoldedAccountId` from the option, replacing
  `GetHoldedAccountIdForCategoryAsync` for the new path.
- Finance's category override becomes an account override.
- `Expenses.md` and the section's tests follow.

## Out of scope

- Spent-vs-budget and the account ledger on the group page (a later Workgroups pull through
  `IHoldedService`).
- Archiving the Holded account when a group ends (`IHoldedClient` has no archive call).
- A `WorkgroupAdmin` role.
