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

Open `/Budget/Summary`. You see the Active Budget Year with **Income** and **Expenses** charts by category (absolute values; when the year projects a surplus, the expenses chart also shows Cash Reserves and Spanish Taxes slices), Total Income / Total Expenses / Net Balance cards, and a utilisation bar.

Line-item detail, salaries, and restricted group contents are never exposed here. Ticketing appears only as aggregated category totals.

If you coordinate any team, a link to `/Budget` appears at the top.

## As a Coordinator

(assumes Volunteer knowledge)

If you coordinate a team linked to a budget category, you have edit rights on that category's line items — nothing more.

### See your department's budget

Open `/Budget`. You see every non-restricted group and its categories — coordinators see peer departments for context. Each category shows allocated amount, line-item total, unallocated remainder, and a progress bar.

You do **not** see groups flagged **Restricted** (typically the Admin group holding staff and meeting costs), nor individual Ticketing line items — ticketing shows up only as summary aggregates for you.

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

From `/Finance` use **Create Budget Year**. On creation, a **Departments** group is auto-populated with one category per team flagged `HasBudget`; a **Ticketing** group is auto-created with four categories (Ticket Revenue, Processing Fees, VAT Liability, Donations) and a zero-defaulted projection; and an **Admin** group is auto-created as **Restricted** so salary and meeting lines stay out of coordinator and public views.

Add further groups and categories from the Finance index. Categories carry an optional CapEx / OpEx flag and an optional linked department — that link drives coordinator edit rights. If you add a department team later, use **Sync Departments** to generate a category for any team with `HasBudget` that does not already have one.

### Lock or unlock a year

A year moves **Draft -> Active -> Closed**. Only one is Active at a time. Closing locks the year — views still work but it is read-only. Archived (soft-deleted) years vanish from non-admin views but remain in the audit log filter; no data is ever hard-deleted.

### Configure the ticketing projection

On the Ticketing group's panel, open **Projection Parameters** to set event start, event date, initial sales count, daily sales rate, average ticket price, VAT rate, and Stripe / TicketTailor fees. Save to project weekly revenue, fees, VAT, and donations through to the event date. The ticketing sync job (daily at 04:30) materialises completed ISO weeks into auto-generated line items; **Sync Actuals** triggers it manually.

### Watch cash flow

`/Finance/CashFlow` aggregates line items by week or month and shows income, expenses, net, and cumulative net, with per-period category breakdown. Items without an expected date appear under **Unscheduled**. Restricted groups and cash-flow-only items (such as ticket-buyer donations) are included — this is the solvency view, not the P&L view.

### Review the audit log

Reachable from the Finance toolbar, filtered by Budget Year. Append-only — entries cannot be edited or deleted, even by Admin.

## Related sections

- [Teams](Teams.md) — categories link to teams; coordinator status drives line-item edit rights.
- [Governance](Governance.md) — FinanceAdmin, Admin, and Board role assignments are managed here.
- [Admin](Admin.md) — Budget Year lifecycle and role assignment sit under admin oversight.
