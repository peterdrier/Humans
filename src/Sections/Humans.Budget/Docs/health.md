# Budget — target shape

Regenerated each section-doctor run (before any scan; see the skill). History at the bottom.

## What the section does

Holds the asociación's plan for a fiscal year's money: how much each department and
workstream intends to raise or spend, in what categories, and when. Finance admins build
and manage the plan (years, groups, categories); department coordinators fill in the line
items for their own departments; every member can see a high-level summary. Ticket-sale
actuals flow in nightly and replace the auto-generated projections (hand-entered line
items are never touched); projected future ticket weeks are re-forecast from those
actuals. Every change to the plan is recorded in an append-only audit trail the Board
can read. Cash-flow views answer "when does the money move, and do we run out?",
including the VAT the association will settle each quarter.

## The shapes

| Question | Surface |
|---|---|
| What does the plan look like, for what I'm allowed to see? | `/Budget`, `/Budget/Summary`, `/Budget/Category/{id}`; `/Finance`, `/Finance/Years/{id}`, `/Finance/Categories/{id}` |
| Change my department's line items (coordinator) | `/Budget/LineItems/*` |
| Manage the plan tree (admin: year lifecycle, groups, categories, line items) | `/Finance/Years/*`, `/Finance/Groups/*`, `/Finance/Categories/*`, `/Finance/LineItems/*`, `/Finance/Admin` |
| Feed ticket actuals in / re-forecast | `/Finance/TicketingBudget/{yearId}/Sync`, `/Finance/TicketingProjection/{groupId}/Update`, `/Finance/Years/{id}/EnsureTicketingGroup`, nightly `budget-ticketing-sync` job |
| When does money move? | `/Finance/CashFlow` (weekly/monthly, VAT settlements, runway) |
| Who changed what? | `/Finance/AuditLog/{yearId?}` |
| Cross-section reads (Expenses, Finance, Tickets, Backdoor; Development's demo seed) | `IBudgetServiceRead`, `IBudgetDemoSeeder` (`Contracts/` folder) |

## Structure

The shapes imply the layered split that exists:

- Two thin controllers — member-facing (`/Budget`) and admin (`/Finance` prefix) — that
  parse, call `IBudgetService`, and redirect with a flash message. Cash-flow *presentation*
  grouping (week/month bucketing of already-computed entries) is view-model shaping and may
  live controller-side; VAT/summary *computation* belongs to the service.
- One `BudgetService`: the tree CRUD pass-through to atomic repository ops, the coordinator
  scope derivation, the pure summary/VAT computations, and the GDPR contributor.
- One `TicketingBudgetService` bridge: aggregates paid orders from `ITicketServiceRead` into
  weekly actuals and hands them to `IBudgetService`; no data of its own. Anything it does
  that never reads Tickets is a pass-through the admin controller could make itself.
- One singleton `BudgetRepository` (`IDbContextFactory`): each mutation is one atomic
  method that writes its audit rows in the same `SaveChanges`. The projected-week
  materialization lives here so it runs against post-sync projection parameters.
- One week-schedule algorithm, `TicketingProjection.CalculateWeeks`, shared by the
  persisted projected line items and the virtual preview.
- The `Contracts/` folder carries only what external callers read: the read methods, the
  seeder hook, the DTO records, the enums.

## Invariants

- At most one `Active` year: activating a year auto-closes any other Active year, with
  audit entries for both transitions — `Data/BudgetRepository.cs:217`.
- Archived years cannot change status — `Data/BudgetRepository.cs:211`; an Active year
  cannot be archived — `Data/BudgetRepository.cs:259`.
- A `Closed` year is read-only: every repository mutation except status change and archive
  gates on it and refuses — `Data/BudgetRepository.cs:1059`.
- Every create/update/delete of a year, group, category, line item, or projection writes a
  `BudgetAuditLog` row in the same `SaveChanges`; the two ticketing paths write one summary
  row only when the change tracker has changes, with a null actor for the nightly job —
  `Data/BudgetRepository.cs:928`. The repository has no audit write surface beyond its two
  private helpers — `Data/BudgetRepository.cs:1212`, `Data/BudgetRepository.cs:1239`.
- Coordinators may write line items only in a category whose `TeamId` is in their effective
  coordinator set (departments they coordinate plus active child teams), never in archived
  years, restricted or ticketing groups — `Authorization/BudgetAuthorizationHandler.cs:35`;
  FinanceAdmin and Admin pass unconditionally — `Authorization/BudgetAuthorizationHandler.cs:29`.
- Non-finance users get `Forbid` on `/Budget/Category/{id}` for any restricted or ticketing
  category, or when they coordinate nothing — `Services/BudgetService.cs:310`; the `/Budget`
  index drops ticketing groups for them — `Views/Budget/Index.cshtml:7`.
- Ticketing sync only upserts auto-generated rows and only removes `Projected: `-prefixed
  auto-generated rows — `Data/BudgetRepository.cs:1167`, `Data/BudgetRepository.cs:1139`.
- GDPR: the actor's audit rows (merge chain included) are exported —
  `Services/BudgetService.cs:849`; they are retained, not erased, under Spanish accounting
  law — `Services/BudgetService.cs:880`.

## Seams

- None open.

## Deliberately not done

- No caching decorator: admin-only, low-traffic (same rationale as Governance/User/Feedback).
- No `I<X>ServiceRead` widening: `IBudgetServiceRead` stays at the methods external
  callers actually call; the other `IBudgetService` members stay internal.
- `ITicketingBudgetService` stays single-member (the job's test seam); the admin controller
  injects the concrete `TicketingBudgetService` for its other calls.
- No cross-domain navs (`Team`, `ResponsibleTeam`, `ActorUser` were deleted, #1188): labels
  are stitched in-memory via `ITeamServiceRead` / `IUserServiceRead`.
- No pagination beyond the audit log's top-500.

## Load-bearing weirdness

- **Projected-week materialization is in the repository**, not the service, so it sees the
  projection parameters updated from actuals in the same `DbContext` (no lag-one-sync).
- **Ticket counts ride in `Notes`**: actual weeks store `"N tickets"`, projected weeks
  `"~N tickets"`, and `GetActualTicketsSold` parses them back. The line items *are* the
  storage; there is no separate actuals table.
- **Generated line items are keyed by description**: the sync matches an existing row by
  its exact English description, so descriptions are written under an English
  `CultureScope` whatever the operator's UI language.
- **`BudgetAdminController` answers `[Route("Finance")]`** — the URL predates the
  Budget/Finance split (#866) and stayed put; action templates are disjoint with
  `Humans.Finance`'s `FinanceController` on the same prefix.
- **`TicketingBudgetSyncJob` is public with an internal constructor**: Shell names the type
  for Hangfire, HUM0034 forbids other public types, so DI registration is a factory in
  `Section.Register` (ruling 43).
- **Processing-fee VAT is a constant 21%** (Spanish IVA on Stripe/TicketTailor fees);
  ticket-revenue VAT comes from the projection row (typically 10).
- **Scaffold names are contracts**: the `Ticket Revenue`/`Processing Fees` category names
  are matched by ordinal string in the sync path; renaming them in the UI breaks the sync's
  category lookup (it logs and no-ops). The groups themselves are found by their
  `IsDepartmentGroup`/`IsTicketingGroup` flags.

## History

| Run | Date | Headline | PR |
|---|---|---|---|
| section-doctor | 2026-08-30 | First pass: doc truth, one home for the VAT math, untested invariants pinned | peterdrier/Humans#1565 |
