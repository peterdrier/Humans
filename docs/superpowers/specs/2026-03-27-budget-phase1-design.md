# Budget Phase 1 — Data Model & Admin Pages

## Summary

Add financial planning to Humans. Phase 1 covers the data model, EF Core migration, and admin management pages for the treasurer/admin to build and manage annual budgets. Coordinators and public views are Phase 2.

## Data Model

### Entity Hierarchy

```
BudgetYear (2026, 2027, ...)
  └── BudgetGroup ("Departments", "Site Infrastructure", "Admin", ...)
        └── BudgetCategory ("Cantina", "Sound", "Art", ...)
              └── BudgetLineItem ("Food", "Drinks", "PA Rental", ...)
```

Four fixed levels. No arbitrary nesting beyond this.

### BudgetYear

| Field | Type | Notes |
|-------|------|-------|
| Id | Guid | Immutable |
| Year | string | Short identifier, e.g., "2026", "2027-A" |
| Name | string | e.g., "2026 — Elsewhere" |
| Status | BudgetYearStatus | Draft / Active / Closed |
| CreatedAt | Instant | NodaTime |
| UpdatedAt | Instant | NodaTime |

One active year at a time. Draft years are not visible outside admin. Closed years are read-only.

### BudgetGroup

| Field | Type | Notes |
|-------|------|-------|
| Id | Guid | Immutable |
| BudgetYearId | Guid | FK → BudgetYear |
| Name | string | e.g., "Departments", "Site Infrastructure" |
| SortOrder | int | Manual ordering |
| IsRestricted | bool | If true, hidden from coordinators/public (e.g., "Admin" group with staff costs) |
| IsDepartmentGroup | bool | If true, categories auto-generated from teams with HasBudget |
| CreatedAt | Instant | NodaTime |
| UpdatedAt | Instant | NodaTime |

### BudgetCategory

| Field | Type | Notes |
|-------|------|-------|
| Id | Guid | Immutable |
| BudgetGroupId | Guid | FK → BudgetGroup |
| Name | string | e.g., "Cantina", "Sound" |
| AllocatedAmount | decimal | The budget allocation for this category |
| ExpenditureType | ExpenditureType | CapEx / OpEx |
| TeamId | Guid? | FK → Team (nullable). Set for department categories, null for non-department categories |
| SortOrder | int | Manual ordering |
| CreatedAt | Instant | NodaTime |
| UpdatedAt | Instant | NodaTime |

Allocation lives here. Line items break down how the allocation is spent. Unallocated remainder = AllocatedAmount minus sum of line item amounts.

### BudgetLineItem

| Field | Type | Notes |
|-------|------|-------|
| Id | Guid | Immutable |
| BudgetCategoryId | Guid | FK → BudgetCategory |
| Description | string | Free text, e.g., "PA System Rental" |
| Amount | decimal | |
| ResponsibleTeamId | Guid? | FK → Team (nullable). Which team is responsible for this line item |
| Notes | string? | Optional notes |
| SortOrder | int | Manual ordering |
| CreatedAt | Instant | NodaTime |
| UpdatedAt | Instant | NodaTime |

### BudgetAuditLog

| Field | Type | Notes |
|-------|------|-------|
| Id | Guid | Immutable |
| BudgetYearId | Guid | FK → BudgetYear |
| EntityType | string | "BudgetYear", "BudgetGroup", "BudgetCategory", "BudgetLineItem" |
| EntityId | Guid | ID of the changed entity |
| FieldName | string? | Which field changed (null for create/delete) |
| OldValue | string? | Previous value (null for create) |
| NewValue | string? | New value (null for delete) |
| Description | string | Human-readable summary |
| ActorUserId | Guid | FK → User |
| OccurredAt | Instant | NodaTime |

Append-only. No UPDATE or DELETE operations.

### Team (existing entity — new column)

| Field | Type | Notes |
|-------|------|-------|
| HasBudget | bool | Default false. When true, team gets a BudgetCategory under the Departments group on year creation |

## Enums

### BudgetYearStatus

```
Draft = 0    // Being built, not visible outside admin
Active = 1   // Current operational budget
Closed = 2   // Year complete, read-only
```

### ExpenditureType

```
CapEx = 0    // Capital expenditure (investments, equipment)
OpEx = 1     // Operational expenditure (recurring costs)
```

## Departments Auto-Mapping

When a BudgetYear is created, the system automatically creates:
1. A "Departments" BudgetGroup with `IsDepartmentGroup = true`
2. A BudgetCategory for each Team where `HasBudget == true`, with `TeamId` linking back to the team

This is a one-time snapshot at year creation, not a live sync. Admin can add/remove/edit department categories afterward.

## Pages

All routes under `/Finance`, served by `FinanceController`.

### GET /Finance

Renders the active year detail page directly (same content as `/Finance/Years/{activeId}`, not a redirect). If no active year exists, shows a prompt to create one or go to Finance Admin.

### GET /Finance/Years/{id}

Year detail page. Shows all groups with their categories. For each category: name, allocated amount, sum of line items, unallocated remainder, expenditure type. Each category links to its detail page. Groups are collapsible. The Departments group shows team names. Actions: add category to a group.

### GET /Finance/Categories/{id}

Category detail page. Shows line items table with description, amount, responsible team, notes. Summary cards: allocated amount, total in line items, unallocated remainder. Actions: add/edit/delete line items.

### GET /Finance/AuditLog/{yearId?}

Filterable change history. Shows who changed what field from what value to what value, when. Optional year filter. Defaults to active year.

### GET /Finance/Admin

Finance configuration page. Create new budget years, set status (Draft/Active/Closed), manage group structure (add/edit/reorder/delete groups for each year). Only one year can be Active at a time — activating a year closes any previously active year.

## Authorization

### New Role: FinanceAdmin

Added to `RoleNames`, `RoleGroups`, `RoleChecks`, and the admin-assignable roles array.

### Controller-Level Auth

```csharp
[Authorize(Roles = RoleGroups.FinanceAdminOrAdmin)]
```

All Finance actions require Admin or FinanceAdmin role.

### Nav Link

Top-level "Finance" entry in the navbar, visible when `RoleChecks.CanAccessFinance(User)` returns true (Admin or FinanceAdmin).

## Audit Trail

All budget mutations (create, update, delete on any budget entity) are logged to `BudgetAuditLog` with:
- The actor's user ID
- The entity type and ID
- The specific field changed (for updates)
- Old and new values (for updates)
- A human-readable description

Audit entries are added to the DbContext before `SaveChangesAsync()` so they're persisted atomically with the business operation.

## Out of Scope (Phase 2+)

- Coordinator view (#233) — read-only access for department coordinators
- Public summary (#234) — pie charts, percentage breakdowns
- Stripe/Holded integration — actuals, transactions
- Cashflow view, invoicing
- Year-over-year rollover
- Visibility enforcement for coordinators/public (the `IsRestricted` and `IsDepartmentGroup` flags are stored but not enforced in Phase 1)
