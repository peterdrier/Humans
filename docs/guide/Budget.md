<!-- freshness:triggers
  src/Humans.Web/Views/Budget/**
  src/Humans.Web/Views/Finance/**
  src/Humans.Web/Controllers/BudgetController.cs
  src/Humans.Web/Controllers/FinanceController.cs
  src/Humans.Application/Services/Budget/**
  src/Humans.Application/Services/Tickets/TicketingBudgetService.cs
  src/Humans.Domain/Entities/BudgetYear.cs
  src/Humans.Domain/Entities/BudgetGroup.cs
  src/Humans.Domain/Entities/BudgetCategory.cs
  src/Humans.Domain/Entities/BudgetLineItem.cs
  src/Humans.Domain/Entities/BudgetAuditLog.cs
  src/Humans.Infrastructure/Data/Configurations/Budget/**
-->
<!-- freshness:flag-on-change
  Year/group/category/line-item structure, FinanceAdmin permissions, ticketing projection, cash flow, and audit log behavior. Review when budget views, services, entities, or EF configurations change.
-->

# Budget

## What this section is for

Budget plans and tracks money across a fiscal year — the single source of truth for allocations, actuals, and audit history. Every change is recorded in an append-only audit log.

The structure is four fixed levels:

```
Budget Year ("2026", "2027-A", ...)
  -> Budget Group ("Departments", "Site Infrastructure", "Admin", ...)
        -> Budget Category ("Cantina", "Sound", "Art", ...)
              -> Budget Line Item ("Food", "PA Rental", ...)
```

Allocations live on the category; line items are the free-text breakdown. Positive amounts are income, negative expense. A line-item VAT rate (0 / 10 / 21 %) projects settlement about six weeks after the end of its quarter.

Only one Budget Year can be **Active** at a time. Years progress **Draft -> Active -> Closed**. Archived years are hidden from non-admin views but their audit history is preserved.

![TODO: screenshot — the Finance index accordion: year selector, groups, categories with budget vs actual, and inline line items]

## Key pages at a glance

- `/Budget/Summary` — public summary: doughnut charts, total cards, utilisation bar. Every authenticated human.
- `/Budget` — coordinator view: non-restricted department budgets with inline line-item editing where you have access.
- `/Finance` — consolidated Finance index: accordion, summary cards, charts. FinanceAdmin and Admin only.
- `/Finance/CashFlow` — weekly or monthly cash-flow projection. FinanceAdmin and Admin only.
- Category and line-item editors open from the accordion via **Manage Line Items**; the audit log (filtered by year) from the Finance toolbar.

## As a Volunteer

### See where the money goes

Open `/Budget/Summary`. You see the Active Budget Year with Total Income / Total Expenses / Net Balance cards and **Income** and **Expenses** doughnut charts by category (absolute values; when the year projects a surplus, the expenses chart also shows Cash Reserves and Spanish Taxes slices), each backed by a breakdown table.

Line-item detail and individual ticketing line items are never exposed here. Restricted groups (e.g. salaries) and ticketing both fold into the category-level aggregates.

If you coordinate a team or are a Finance Admin / Admin, a **Department Detail** link to `/Budget` appears at the top.

## As a Coordinator

(assumes Volunteer knowledge)

If you coordinate a team linked to a budget category, you have edit rights on that category's line items — nothing more.

### See your department's budget

Open `/Budget`. You see every non-ticketing group and its categories — coordinators see peer departments for context. Each category shows allocated amount, line-item total, unallocated remainder, and an expenditure-type badge.

Restricted groups (typically the Admin group holding staff and meeting costs) appear with a **Restricted** badge — you can see the group name and category names but cannot drill in. Ticketing groups are hidden from this view entirely; ticketing only shows up as summary aggregates in `/Budget/Summary`.

### Add, edit, and remove line items

Inside a category linked to a team you coordinate, use **Add Line Item** or inline edit controls. Each line item has a description, an amount (positive income, negative expense), an optional expected date that feeds the cash-flow projection, a VAT rate, an optional responsible team, and optional notes.

Every edit is audit-logged. You cannot change the category's allocated amount or CapEx / OpEx flag — those belong to FinanceAdmin. Coordinator access follows child teams: if you coordinate a department, you can edit line items on its sub-teams' categories too.

### Track actuals against your plan

Each category shows budget vs actual with the unallocated remainder. Auto-generated line items (weekly ticket-sales rollups, for example) are marked **Auto** on a lighter row — don't edit those by hand; they are overwritten on the next sync.

## As a Board member / Admin (Finance Admin)

(assumes Coordinator knowledge)

Financial structure, year lifecycle, restricted groups, and the audit log live with FinanceAdmin and Admin. Board sees the full budget — restricted groups and salaries included — for oversight, and approves the total; day-to-day editing is done by FinanceAdmin.

**FinanceAdmin is the app's Treasurer role.** Assignments live on the human detail page (see [Governance](Governance.md)) and are granted by Board or Admin.

### Create and structure a Budget Year

From the `/Finance/Admin` page use **Create Budget Year**. On creation, a **Departments** group is auto-populated with one category per team flagged `HasBudget`, and a **Ticketing** group is auto-created with two starter categories (**Ticket Revenue** and **Processing Fees**) plus a zero-defaulted projection row. Neither auto-created group is marked Restricted — create a Restricted group manually (typically named "Admin") if you need to keep salary and meeting lines out of coordinator and public drill-in views.

Add further groups and categories from the Finance year detail. Categories carry a CapEx / OpEx flag (default OpEx) and an optional linked team — that link drives coordinator edit rights. If you add a budget-flagged team later, use **Sync Departments** to generate a category for any team with `HasBudget` that does not already have one in the Departments group. If you create a year without ticketing or remove the ticketing group, **Add Ticketing Group** on the year detail re-creates it.

### Lock or unlock a year

A year moves **Draft -> Active -> Closed**. Only one is Active at a time. Closing locks the year — views still work but it is read-only. Archived (soft-deleted) years vanish from non-admin views but remain in the audit log filter; no data is ever hard-deleted.

### Configure the ticketing projection

On the Ticketing group's panel, open **Projection Parameters** to set event start, event date, initial sales count, daily sales rate, average ticket price, VAT rate, and Stripe / TicketTailor fees. Saving rebuilds projected weekly revenue and per-week Stripe / TicketTailor fee lines through to the event date (VAT settles automatically in the cash-flow view from each line's VAT rate — it is not stored as its own line item). The ticketing sync job (`TicketingBudgetSyncJob`, daily at 04:30 cron `30 4 * * *`) materialises completed ISO weeks of actual sales into auto-generated line items and refreshes the remaining-week projections in the same pass; **Sync Actuals** on the year detail triggers it manually.

### Watch cash flow

`/Finance/CashFlow` aggregates line items by week or month and shows income, expenses, net, and cumulative net, with per-period category breakdown. Items without an expected date appear under **Unscheduled**. Restricted groups and cash-flow-only items (such as ticket-buyer donations) are included — this is the solvency view, not the P&L view.

### Review the audit log

Reachable from the Finance toolbar, filtered by Budget Year. Append-only — entries cannot be edited or deleted, even by Admin.

## Related sections

- [Teams](Teams.md) — categories link to teams; coordinator status drives line-item edit rights.
- [Governance](Governance.md) — FinanceAdmin, Admin, and Board role assignments are managed here.
- [Admin](Admin.md) — Budget Year lifecycle and role assignment sit under admin oversight.
