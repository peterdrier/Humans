# Budget — Section Invariants

## Concepts

- A **Budget Year** is the top-level container for a fiscal year's budget. It progresses through Draft, Active, and Closed stages.
- A **Budget Group** is a second-level grouping within a year (e.g., "Departments", "Site Infrastructure"). Groups can be flagged as **restricted**, hiding them from coordinators.
- A **Budget Category** is a third-level container within a group that holds the allocated budget amount. Categories can be linked to a department.
- A **Budget Line Item** is a detail row within a category — a free-text description with an amount, expenditure type (CapEx or OpEx), and an optional responsible team.
- The **Audit Log** is an append-only record of every field-level change to budget entities.

## Actors & Roles

| Actor | Capabilities |
|-------|-------------|
| Any active human | View a read-only summary of the active budget year |
| Department coordinator | View the full budget for the active year. Create, edit, and delete line items within categories linked to a department they coordinate |
| FinanceAdmin, Admin | Full management of all budget years, groups, categories, and line items. View audit log. View cash flow projections. Sync departments (auto-create categories for departments that lack one). Manage restricted groups |

## Invariants

- A budget year follows the lifecycle: Draft then Active then Closed. Only one year can be Active at a time.
- A coordinator can only create, edit, or delete line items in categories linked to a department they coordinate.
- Restricted groups are only visible and editable by FinanceAdmin and Admin. Coordinators cannot see them.
- Ticketing groups and their category details are only accessible to FinanceAdmin and Admin. Ticketing income/expenses still appear in the public budget summary aggregates, but coordinators cannot view individual ticketing categories or line items.
- Every create, update, or delete on a group, category, or line item generates an audit log entry recording old value, new value, actor, and timestamp.
- "Sync Departments" creates a category for each department that does not already have one in the selected year.

- The `/Finance` index shows a consolidated accordion view: groups, categories with budget vs actual comparison, and inline line items. FinanceAdmin sees all summary data inline.
- The `/Finance/CashFlow` view aggregates line items by time period (weekly/monthly) and shows running net.

## Negative Access Rules

- Regular humans **cannot** edit any budget data. They can only view the summary.
- Coordinators **cannot** edit budget groups, categories, or restricted groups. They can only manage line items in categories linked to their own department.
- Coordinators **cannot** see restricted budget groups.
- Coordinators **cannot** see ticketing group details (categories or line items). Ticketing data only appears in summary aggregates.
- Coordinators **cannot** create, activate, or close budget years.

## Triggers

- Every mutation to budget groups, categories, or line items generates an append-only audit log entry.

## Cross-Section Dependencies

- **Teams**: Budget categories can be linked to a department. Coordinator status on the department determines line item editing access.
- **Admin**: Budget year lifecycle management is restricted to FinanceAdmin and Admin.
