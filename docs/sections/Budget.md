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

## Architecture — Current vs Target

See `docs/architecture/design-rules.md` for the full rules.

**Owning services:** `BudgetService`
**Owned tables:** `budget_years`, `budget_groups`, `budget_categories`, `budget_line_items`, `budget_audit_logs`, `ticketing_projections`

## Target Architecture Direction

> **Status:** This section currently follows the "services in Infrastructure, direct DbContext" model. It will be migrated to the repository/store/decorator pattern per [`../architecture/design-rules.md`](../architecture/design-rules.md). **Delete this block once the migration lands and this section's services live in `Humans.Application` with `*Repository.cs` impls in `Humans.Infrastructure/Repositories/`.**

### Target repositories

- **`IBudgetRepository`** — owns `budget_years`, `budget_groups`, `budget_categories`, `budget_line_items`, `budget_audit_logs`, `ticketing_projections`
  - Aggregate-local navs kept: `BudgetYear.Groups`, `BudgetYear.AuditLogs`, `BudgetGroup.BudgetYear`, `BudgetGroup.Categories`, `BudgetGroup.TicketingProjection`, `BudgetCategory.BudgetGroup`, `BudgetCategory.LineItems`, `BudgetLineItem.BudgetCategory`, `BudgetAuditLog.BudgetYear`, `TicketingProjection.BudgetGroup`
  - Cross-domain navs stripped: `BudgetCategory.Team` → `BudgetCategory.TeamId` only (Teams domain); `BudgetLineItem.ResponsibleTeam` → `BudgetLineItem.ResponsibleTeamId` only (Teams domain); `BudgetAuditLog.ActorUser` → `BudgetAuditLog.ActorUserId` only (Users/Identity domain)
  - Note: `budget_audit_logs` is append-only per §12 — repository exposes `AddAsync` and `GetXxxAsync` but no `UpdateAsync`/`DeleteAsync`.

### Current violations

Observed in this section's service code as of 2026-04-15:

- **Cross-domain `.Include()` calls:**
  - `BudgetService.cs:542` — `.Include(c => c.Team)` (Teams domain) when loading a category detail
  - `BudgetService.cs:867` — `.Include(a => a.ActorUser)` (Users/Identity domain) when loading the audit log
- **Cross-section direct DbContext reads:**
  - `BudgetService.cs:107` — `_dbContext.Teams.Where(t => t.HasBudget && t.IsActive)` during budget year creation (Teams section)
  - `BudgetService.cs:321` — `_dbContext.Teams.Where(...)` during Sync Departments (Teams section)
  - `BudgetService.cs:922` — `_dbContext.TeamMembers` in `GetEffectiveCoordinatorTeamIdsAsync` (Teams section)
  - `BudgetService.cs:930` — `_dbContext.Set<TeamRoleAssignment>()` with deep `.TeamMember` / `.TeamRoleDefinition.Team` nav traversal in `GetEffectiveCoordinatorTeamIdsAsync` (Teams section)
  - `BudgetService.cs:945` — `_dbContext.Teams.Where(t => t.ParentTeamId != null ...)` for child-team expansion (Teams section)
  - ~~`TicketingBudgetService.cs:44` — `_dbContext.TicketOrders` (Tickets section)~~ **Resolved in PR #545b (2026-04-22):** `TicketingBudgetService` moved to `Humans.Application.Services.Tickets` and now reads paid orders via the Tickets-owned `ITicketingBudgetRepository`. Budget no longer has a code path that reads Tickets tables directly.
- **Within-section cross-service direct DbContext reads:** None found.
- **Inline `IMemoryCache` usage in service methods:** None found.
- **Cross-domain nav properties on this section's entities:**
  - `BudgetCategory.Team` → Teams domain
  - `BudgetLineItem.ResponsibleTeam` → Teams domain
  - `BudgetAuditLog.ActorUser` → Users/Identity domain

### Touch-and-clean guidance

Until this section is migrated end-to-end, when touching its code:

- Do **not** add new `.Include()` calls that traverse into `Team`, `ResponsibleTeam`, `ActorUser`, or any other non-Budget entity. If you need a team name or actor display name alongside budget data, load the Budget aggregate first, then call `ITeamService` / `IUserService` to stitch the labels in memory.
- When adding new coordinator/permission checks, do **not** grow `GetEffectiveCoordinatorTeamIdsAsync` (`BudgetService.cs:919`) with more Teams-table queries. That method is the largest cross-section bleed in the file and should migrate behind `ITeamService.GetEffectiveCoordinatorTeamIdsAsync(userId)` on the Teams side — push new logic there instead.
- New cross-section reads must go through the owning service interface (`ITeamService`, `ITicketQueryService`, `IUserService`), never `_dbContext`. Treat any new `_dbContext.Teams` / `_dbContext.TeamMembers` / `_dbContext.TicketOrders` call in Budget services as a regression.
- Keep new audit-log writes using `AddAsync`-only semantics — never `Update` or `Remove` a `BudgetAuditLog` row, even in cleanup code (§12).
