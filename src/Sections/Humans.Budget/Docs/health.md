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
can read — except the two ticketing sync paths, currently outside it (see Seams). Cash-flow views answer "when does the money move, and do
we run out?" including the VAT the association will settle each quarter.

## The shapes

| Question | Surface |
|---|---|
| What does the plan look like, for what I'm allowed to see? | `/Budget`, `/Budget/Summary`, `/Budget/Category/{id}`; `/Finance`, `/Finance/Years/{id}`, `/Finance/Categories/{id}` |
| Change my department's line items (coordinator) | `/Budget/LineItems/*` |
| Manage the plan tree (admin: year lifecycle, groups, categories, line items) | `/Finance/Years/*`, `/Finance/Groups/*`, `/Finance/Categories/*`, `/Finance/LineItems/*`, `/Finance/Admin` |
| Feed ticket actuals in / re-forecast | `/Finance/TicketingBudget/{yearId}/Sync`, `/Finance/TicketingProjection/{groupId}/Update`, `/Finance/Years/{id}/EnsureTicketingGroup`, nightly `budget-ticketing-sync` job |
| When does money move? | `/Finance/CashFlow` (weekly/monthly, VAT settlements, runway) |
| Who changed what? | `/Finance/AuditLog/{yearId?}` |
| Cross-section reads (Expenses, Base ticket query, Shell seeder) | `IBudgetServiceRead`, `IBudgetDemoSeeder` (contracts leaf) |

## Structure

The shapes imply exactly the layered split that exists:

- Two thin controllers — member-facing (`/Budget`) and admin (`/Finance` prefix) — that
  parse, call `IBudgetService`, and redirect with a flash message. Cash-flow *presentation*
  grouping (week/month bucketing of already-computed entries) is view-model shaping and may
  live controller-side; VAT/summary *computation* belongs to the service.
- One `BudgetService`: the tree CRUD pass-through to atomic repository ops, the coordinator
  scope derivation, the pure summary/VAT computations, and the GDPR contributor.
- One `TicketingBudgetService` bridge: aggregates paid orders from `ITicketServiceRead` into
  weekly actuals and hands them to `IBudgetService`; no data of its own.
- One singleton `BudgetRepository` (`IDbContextFactory`): each mutation is one atomic
  method that writes its audit rows in the same `SaveChanges`. The projected-week
  materialization lives here so it runs against post-sync projection parameters.
- Contracts leaf carries only what external callers read: the read methods, the seeder
  hook, the DTO records, the enums.

## Invariants

- At most one `Active` year: activating a Draft auto-closes any other Active year, with
  audit entries for both transitions.
- A `Closed` year is read-only: repository mutations gate on it and refuse (the ticketing
  sync pair and the year-metadata rename sit outside the gate today — see Seams).
- Every create/update/delete of a year, group, category, line item, or projection writes a
  `BudgetAuditLog` row in the same transaction; the log is append-only (no update/delete
  surface exists, §12).
- Coordinators may write line items only in categories whose `TeamId` is a department they
  coordinate (or a child of it), and never in restricted or ticketing groups; the
  resource-based `BudgetAuthorizationHandler` is the single gate for those writes.
- Restricted groups: visible to coordinators as headers/category names only, no drill-in
  (`/Budget/Category/{id}` → Forbid); ticketing groups: hidden from `/Budget` entirely for
  non-finance users, drill-in also Forbid. Both still roll up into `/Budget/Summary`.
- Year deletion is soft (archive + Closed); an Active year cannot be archived.
- Ticketing sync only upserts auto-generated items and only removes `Projected: `-prefixed
  ones; hand-entered line items are never touched by the sync.
- GDPR: audit rows are exported for the actor (chain-following merge tombstones) and
  retained under Spanish accounting law rather than erased, with the declaration saying so.

## Seams

- **The ticketing paths sit outside the section's cross-cutting guarantees.** This is one
  class, not one-offs: `SyncTicketingActualsAsync` and `RefreshTicketingProjectionsAsync`
  neither call the closed-year gate (finding 5) nor write `BudgetAuditLog` rows for the
  line items they mutate (finding 22) — the nightly job and admin refresh can change a
  closed year's ticketing items invisibly. Bring the pair under the guarantees or exempt
  them in the invariants — Peter's call (2026-08-30 run, peterdrier/Humans#1565); neither
  side changed until then.
- **Closed-year metadata edits are ungated.** `UpdateYearAsync` — the Edit Year rename
  form, which renders for Closed years too — never calls the closed-year gate, so a Closed
  year's identifier and name can still change (audited, but ungated; finding 23). Same
  ruling class as finding 5: gate it or exempt year-metadata edits — Peter's call, neither
  side changed until then.
- **Ticketing-group deny vs the handler.** The coordinator invariant's "never in ticketing
  groups" half is not enforced: `BudgetAuthorizationHandler` has no `IsTicketingGroup`
  check, masked today by null `TeamId` on scaffolded ticketing categories (finding 6, same
  run and PR).
- (The 2026-08-18 `NoActiveYear.cshtml` missing-links debt was found already
  built — `HoldedAccounts`, `HoldedUnmatched`, `Creditors` links exist — and its
  `Docs/debt.yml` entry removed this run.)

## Deliberately not done

- No caching decorator: admin-only, low-traffic (same rationale as Governance/User/Feedback).
- No `I<X>ServiceRead` widening: the contracts leaf stays at exactly the methods Expenses
  and the Base consumers actually call; the other `IBudgetService` members stay internal.
- `ITicketingBudgetService` stays single-member (the job's test seam); the admin controller
  deliberately injects the concrete `TicketingBudgetService` for its other calls.
- No cross-domain navs (`Team`, `ResponsibleTeam`, `ActorUser` were deleted, #1188): labels
  are stitched in-memory via `ITeamServiceRead` / `IUserServiceRead`.
- No pagination beyond the audit log's top-500.

## Load-bearing weirdness

- **Projected-week math is in the repository**, not the service, so re-materialization sees
  the projection parameters updated in the same `DbContext` (no lag-one-sync). The service
  keeps a *duplicate* of the week-schedule loop for the virtual (non-persisted) forecast —
  two copies of the same algorithm is the accepted cost today.
- **Ticket counts ride in `Notes`**: actual weeks store `"N tickets"`, projected weeks
  `"~N tickets"`, and `GetActualTicketsSold` parses them back. The line items *are* the
  storage; there is no separate actuals table.
- **`BudgetAdminController` answers `[Route("Finance")]`** — the URL predates the
  Budget/Finance split (#866) and stayed put; action templates are disjoint with
  `Humans.Finance`'s `FinanceController` on the same prefix.
- **Admin nav names the controller `BudgetAdmin` but labels it "Finance"** — the tag helper
  resolves controller names, not routes.
- **`TicketingBudgetSyncJob` is public with an internal constructor**: Shell names the type
  for Hangfire, HUM0034 forbids other public types, so DI registration is a factory in
  `Section.Register` (ruling 43).
- **Processing-fee VAT is a constant 21%** (Spanish IVA on Stripe/TicketTailor fees);
  ticket-revenue VAT comes from the projection row (typically 10).
- **Scaffold names are contracts**: `Departments`/`Ticketing` group flags and the
  `Ticket Revenue`/`Processing Fees` category names are matched by ordinal string in the
  sync path; renaming them in the UI breaks the sync's category lookup (it logs and
  no-ops).

## History

| Run | Date | Headline | PR |
|---|---|---|---|
| section-doctor | 2026-08-30 | First pass: doc truth, one home for the VAT math, three untested invariants pinned | peterdrier/Humans#1565 |
