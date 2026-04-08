# Budget

## Business Context

Nobodies Collective runs one major event per year (2026: "Elsewhere", historically "Nowhere"). Financial management currently lives in spreadsheets and the treasurer's head. With the new association lacking a cash buffer, there is real solvency concern in this first year. The Budget section brings financial planning, tracking, and transparency into Humans.

**Goals:**
- Give the treasurer/board confidence in solvency at any point in the season
- Let department coordinators own their budgets without hand-holding
- Show the community where their ticket money goes
- Enable year-over-year planning with CapEx/OpEx separation
- Replace the spreadsheet as the single source of truth for financial planning

**Non-goals:**
- Not an accounting system (no tax filing, no double-entry ledger)
- Not a payment system (Stripe handles money movement)
- Cashflow is reporting only, not decision-making
- No procurement/approval workflow (accountability is social, not systemic)
- Not multi-event (one budget per year)
- Not real-time (daily sync cadence is sufficient)

**Trade-offs accepted:** Simplicity over accounting rigor. Manual data entry OK where integrations don't exist yet. Coarse-grained categories initially.

## Annual Budget Cycle

| When | Phase | Key Activities |
|------|-------|---------------|
| Aug-Sep | Post-event | Capture problems, broken equipment, improvement ideas |
| Oct-Nov | CapEx ideation | Departments surface investment proposals (future) |
| Dec-Jan | Budget shakedown | Roll over OpEx, finalize CapEx decisions, set department allocations |
| Feb-Mar | Ticketing & pricing | Budget informs ticket price; sales begin |
| Apr-Jun | Spending season | Purchases, contracts, actuals flowing in |
| Jul | Event | The event itself |
| Ongoing | Quarterly | IVA, salary tax obligations (accountant-handled, but visible) |

## Stakeholders & Visibility

### Role Map

| Role | Creates/Edits | Sees | Key Need |
|------|--------------|------|----------|
| FinanceAdmin/Admin | Total budget, all allocations, overhead, invoices | Everything | Solvency confidence, single source of truth |
| Board | Approve total budget | Everything incl. salaries/overhead | Governance oversight |
| Dept. Coordinators | Their dept. line items | All department budgets, own % spent | Own their allocation, see peer context |
| General members | Nothing | Public summary (pie charts, speedometers) | Trust, transparency |
| Accountant | Nothing (external) | N/A — uses Stripe/Holded/bank directly | — |

### Visibility Tiers

| Tier | Content | Audience |
|------|---------|----------|
| Full | All groups + overhead + salaries + cashflow | Board, Admin/FinanceAdmin |
| Coordinator | All department budgets + own % spent, no overhead/salaries | Department Coordinators |
| Public | Aggregated summary, metaphors ("X of your ticket goes to...") | All members |

## Data Model

### Budget Hierarchy (Four Fixed Levels)

```
BudgetYear ("2026", "2027-A", ...)
  └── BudgetGroup ("Departments", "Site Infrastructure", "Admin", ...)
        └── BudgetCategory ("Cantina", "Sound", "Art", ...)
              └── BudgetLineItem ("Food", "Drinks", "PA Rental", ...)
```

- `BudgetYear.Year` is a string (not int) for flexibility (e.g., "2027-A")
- Top-level groups include a special "Departments" group (`IsDepartmentGroup = true`) that auto-generates categories from teams with `HasBudget == true` on year creation
- A "Ticketing" group (`IsTicketingGroup = true`) is auto-created per year with a `TicketingProjection` for configurable revenue/fee projection parameters
- An "Admin" group with `IsRestricted = true` hides sensitive items (staff, meetings) from coordinators/public
- Allocation lives on `BudgetCategory` — line items are the free-text breakdown
- Each line item has: description, amount, responsible team (FK → Team), optional notes
- CapEx/OpEx flag is on `BudgetCategory`, not line items
- No arbitrary nesting beyond four levels
- `BudgetYear` supports soft-delete (`IsDeleted`, `DeletedAt`): "deleting" a year archives it instead of removing data, preserving all audit log history. Archived years are hidden from non-admin views but remain visible on the Finance Admin page and in the audit log year filter.

### Budget Audit Log

All budget changes are logged:
- Who made the change
- What was changed (entity, field, old value, new value)
- When (timestamp)

Example: "Daniela changed Sound > Equipment Rental budget from 20,000 to 23,000 on 2026-02-17"

### Transactions (Actuals)

Transactions are pulled from external systems and stored locally:
- Source (Stripe / Holded / Manual)
- Amount, date, description
- Mapped budget category (manual assignment initially, auto-mapping later)
- Raw metadata from source system

### Invoices

Outbound invoices to members/barrios:
- Ticket purchases (with donation component)
- Barrio services (water, trash, butane, container storage)

## External Integrations

| System | Direction | Data | Integration |
|--------|-----------|------|-------------|
| Stripe | Inbound | Ticket income, donation amounts | API, background sync job |
| Holded | Inbound | Expense data | API, background sync job (pending API investigation) |
| Bank account | None | Managed by accountant externally | — |

### Integration Approach

- Background sync jobs (consistent with existing Google sync job pattern)
- Daily or on-schedule cadence
- Data stored locally for resilience
- Manual transaction-to-category mapping initially
- Stripe: clean 1:1 product-to-category mapping expected
- Holded: receipt-based, will need manual help/logic; auto-mapping rules built over time as patterns emerge

## User Stories

### V1: Budget Structure & Planning

**As a treasurer**, I want to create an annual budget with groups, categories, and line items so that the organization's financial plan is structured and visible.
- Acceptance: Can recreate existing spreadsheet budget in the app
- Acceptance: Budget changes are audit-logged with who/what/when

**As a department coordinator**, I want to see all department budgets and edit my own line items so that I own my department's financial planning.
- Acceptance: Coordinator can view all department groups
- Acceptance: Coordinator can edit only their own department's line items
- Acceptance: Overhead/salary group is not visible to coordinators

**As an admin**, I want budget editing restricted by role with a full audit trail so that changes are accountable.
- Acceptance: Only Admin/Treasurer can edit top-level allocations
- Acceptance: Audit log shows all changes with old/new values

### V2: Actuals & Public View

**As a general member**, I want to see a high-level summary of where my ticket money goes so that I trust how the organization spends funds.
- Acceptance: Public view shows pie chart / percentage breakdown
- Acceptance: No salary or line-item detail visible
- Acceptance: Speedometer-style % spent indicator

**As a treasurer**, I want Stripe income data to flow into the budget automatically so that I can see actual income against planned income.
- Acceptance: Stripe sync job pulls payment data on schedule
- Acceptance: Transactions appear mapped to budget categories
- Acceptance: Budget vs actuals view shows planned vs actual per category

**As a treasurer**, I want to see a cashflow view (income vs expenses over time) so that I can monitor solvency throughout the season.
- Acceptance: Line chart or table showing monthly in/out
- Acceptance: Current cash position visible at a glance

**As a treasurer**, I want to create invoices for ticket purchases and barrio services so that revenue collection is tracked in one place.
- Acceptance: Can generate invoice with line items
- Acceptance: Invoice tracks payment status

### V3: Year-over-Year

**As a treasurer**, I want to roll forward last year's budget structure to start next year's planning so that annual planning is fast.
- Acceptance: Clone budget structure with previous year's actuals as reference
- Acceptance: CapEx/OpEx separation enables different rollover logic

**As a department coordinator**, I want to log future investment ideas so that the community can prioritize capital improvements in the fall.
- Acceptance: Can create CapEx project proposals with description and estimated cost

## Architecture Decisions

### ADR-1: Three Fixed Hierarchy Levels
- **Decision:** BudgetYear > BudgetGroup > BudgetCategory > BudgetLineItem. No arbitrary nesting.
- **Rationale:** Matches existing spreadsheet structure. Fixed depth avoids tree-query complexity.
- **Consequence:** If a 4th level is needed, it's a line item description, not structural.

### ADR-2: Live Edits with Audit Trail
- **Decision:** Single current budget per year. Changes logged in audit table (who/what/when/old/new).
- **Rationale:** Matches stated need for accountability without version management overhead.
- **Consequence:** No version comparison feature; audit log provides change history.

### ADR-3: Background Sync Jobs for Actuals
- **Decision:** Scheduled jobs pull from Stripe/Holded, store locally. Follows existing sync job pattern.
- **Rationale:** Consistent architecture, resilient to external API downtime.
- **Consequence:** Data up to ~24h stale. Acceptable.

### ADR-4: Manual Categorization First, Auto-Mapping Later
- **Decision:** Treasurer manually assigns transactions to budget categories. Data model supports future rule-based mapping.
- **Rationale:** Stripe is clean (1:1). Holded is receipt-based and needs human judgment initially.
- **Consequence:** Manual effort for treasurer in 2026. Build auto-mapping rules for 2027+.

## Implementation Phases

### V1: Budget Structure & Planning — IMPLEMENTED (PR #50)
- Data model: BudgetYear, BudgetGroup, BudgetCategory, BudgetLineItem, BudgetAuditLog
- EF Core migration with HasBudget on Team for department auto-mapping
- FinanceAdmin role (replaces "Treasurer" in original spec)
- Budget CRUD at `/Finance/*` via FinanceController
- Field-level audit trail with old/new values (separate BudgetAuditLog table)
- Nav link visible to FinanceAdmin + Admin
- **Exit:** Can recreate 2026 spreadsheet budget in the app

### V1b: Coordinator View & Public Summary — IMPLEMENTED (#233, #234)
- **Coordinator view** at `/Budget` via BudgetController (#233):
  - Any team coordinator sees all department budgets (non-restricted groups)
  - Can add/edit/delete line items within own department only
  - Per-category unallocated remainder and progress bar
  - `IsRestricted` groups hidden from coordinator view
  - Authorization uses resource-based auth via `BudgetAuthorizationHandler` + `BudgetOperationRequirement.Edit`, which delegates to `GetEffectiveCoordinatorTeamIdsAsync` (includes child teams)
  - All changes audit-logged via existing BudgetAuditLog
- **Public summary** at `/Budget/Summary` (#234):
  - All authenticated members see budget allocation pie chart (Chart.js doughnut)
  - Progress bar showing overall budget utilization
  - Breakdown table with category amounts and percentages
  - No line-item, salary, or overhead detail visible
  - Coordinators see link to department detail view
- **Nav:** "Budget" link visible to all authenticated users; "Finance" link remains FinanceAdmin-only
- **Exit:** Coordinators can manage their department budgets; all members see where money goes

### V1c: Sign Convention and VAT Tracking — IMPLEMENTED (#282, #283)
- **Sign convention:** Positive amounts = income, negative amounts = expenses
  - Removed `min=0` constraints on all budget amount inputs
  - Both `BudgetCategory.AllocatedAmount` and `BudgetLineItem.Amount` accept negative values
- **VAT/IVA tracking:** `VatRate` int on `BudgetLineItem` (0%, 10%, 21%)
  - Virtual VAT entries computed for line items with VatRate > 0 and an ExpectedDate
  - VAT has converse sign: income generates VAT liability (expense), expenses generate VAT credit (income)
  - Settlement date ~6 weeks after end of quarter containing the expected date
  - VAT projections shown in category detail views with visual distinction (italicized, badges)
  - VAT cash flows included in summary overview totals and charts
- **Dual overview charts** on `/Budget/Summary`:
  - Income chart (categories with positive totals)
  - Expenses chart (absolute values of negative totals)
  - When profitable, expenses chart includes Cash Reserves (90%) and Spanish Taxes (10%) slices
- **Summary cards:** Total Income, Total Expenses, Net Balance
- **EF migration** adds `VatRate` column to `budget_line_items`
- **Donations:** Deferred — donations come from ticket overpayments and direct external sources, not budget allocations. Will be modeled separately when Stripe integration lands.
- **Exit:** Budget system supports income vs expense distinction and VAT projections

### V1d: Ticketing Budget Projection — IMPLEMENTED (#284)
- **Ticketing group** auto-created on budget year creation (`IsTicketingGroup = true`)
  - Contains four auto-created categories: Ticket Revenue, Processing Fees, VAT Liability, Donations
  - `TicketingProjection` entity (1:1 with BudgetGroup) stores configurable parameters:
    Start Date, Event Date, Initial Sales Count, Daily Sales Rate, Avg Ticket Price, VAT Rate,
    Stripe Fee %, Stripe Fixed Fee, TicketTailor Fee %
  - All parameters default to 0 — configured by FinanceAdmin via projection form on YearDetail
- **Actuals materialization** via `TicketingBudgetSyncJob` (daily at 04:30):
  - Completed ISO weeks (Mon-Sun) generate auto-generated (`IsAutoGenerated`) line items per week
  - Revenue: "+€X" with ticket count in notes
  - Stripe fees: "-€X" from real Stripe fee data
  - TicketTailor fees: "-€X" from real ApplicationFee data
  - VAT: "-€X" computed from order VAT amounts
  - Donations: "+€X" flagged as `IsCashflowOnly` — excluded from income/expense/profit
  - Quarter-boundary weeks are split for correct VAT attribution
  - Upsert logic: existing auto-generated items updated on re-sync, not duplicated
- **Projections** via `ITicketingBudgetService.GetProjectionsAsync`:
  - Virtual (non-persisted) weekly entries for future weeks from current week to event date
  - Recalculates from latest actuals: remaining tickets / remaining days = projected daily rate
  - Observed donation rate from actuals applied to projections
  - Fees computed on revenue + donations; VAT on revenue only (inclusive formula)
- **Budget totals** exclude `IsCashflowOnly` line items from income/expense/profit/charts
- **UI changes:**
  - Ticketing group badge ("Ticketing") on year detail views
  - Projection parameters form (collapsible) with Save button
  - "Sync Actuals" button for manual trigger
  - Auto-generated line items visually distinct: `table-light` row, "Auto" badge
  - Cashflow-only items show "Cashflow" badge
- **New properties on BudgetLineItem:** `IsAutoGenerated` (bool), `IsCashflowOnly` (bool)
- **New property on BudgetGroup:** `IsTicketingGroup` (bool)
- **EF migration:** `AddTicketingProjectionAndBudgetFlags`
- **Exit:** Ticket sales actuals flow into budget as weekly line items; projections available for planning

### V1e: Finance Consolidation — IMPLEMENTED (#325)
- **Consolidated `/Finance` index** (YearDetail view):
  - Tree-style accordion: year selector, groups as expandable sections, categories within each group, line items within each category
  - Each category shows `AllocatedAmount` as budget target alongside actual line item totals with remaining amount
  - Negative `AllocatedAmount` displayed as "Planned spend" with absolute values; positive as "Income target"
  - Inline summary cards (Total Income, Total Expenses, Net Balance) and collapsible summary charts
  - FinanceAdmin sees all data on one page without navigating to `/Budget/Summary`
  - Category detail pages linked from "Manage Line Items" within accordion
- **No changes** to `/Budget` coordinator view or `/Budget/Summary` public view
- **Exit:** FinanceAdmin has a unified single-page overview of the entire budget

### V1f: Cash Flow Projection — IMPLEMENTED (#347)
- **Cash flow view** at `/Finance/CashFlow`:
  - Aggregates line items by time period (weekly or monthly, toggle-able)
  - Each period row shows income total, expense total, net, and running cumulative net
  - Category-level breakdown within each period (expandable)
  - Items without `ExpectedDate` shown in separate "Unscheduled" section
  - Includes `IsCashflowOnly` items (relevant to actual cash movement)
  - Restricted groups visible (FinanceAdmin/Admin only page)
  - Accessible from Finance section toolbar
- **Exit:** FinanceAdmin can see when money comes in and goes out across the budget year

### V2b: Stripe Integration (2-3 sessions)
- Stripe sync job
- Transaction storage and manual category mapping
- Budget vs actuals view
- **Exit:** Ticket sale income appears against Ticketing budget group

### V2c: Holded Integration (1-3 sessions)
- Prerequisite: Holded API investigation
- Holded sync job
- Expense transaction mapping
- **Exit:** Expenses flow in and can be mapped to budget lines

### V2d: Invoicing (1-2 sessions)
- Invoice creation for tickets and barrio services
- **Exit:** Treasurer can generate invoices

### V3: Year-over-Year (Fall 2026)
- Budget rollover (clone structure from previous year)
- CapEx project idea capture by department
- Bottom-up proposal flow
- **Exit:** 2027 budget can start from 2026 data

## Season Milestones

| Milestone | Target | Unlocks |
|-----------|--------|---------|
| V1 complete — budget in the app | ASAP | Coordinators start using it |
| Holded API investigation | Before V2c | Confirms integration approach |
| Stripe actuals flowing | Before event (Jul 2026) | Live income tracking during ticket sales |
| Cashflow view live | Before event (Jul 2026) | Solvency visibility during peak spend |
| V3 kickoff | Post-event (Sep 2026) | 2027 planning cycle |

## Related Features

- **Shift Management** (25): Department coordinators overlap with budget coordinators
- **Teams** (06): Department structure aligns with team structure
- **Ticket Vendor Integration** (24): Stripe income source for budget actuals
- **Audit Log** (12): Existing audit patterns may inform budget audit trail design
