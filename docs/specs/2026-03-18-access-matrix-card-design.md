# Access Matrix Card — Design Specification

**March 18, 2026 | v1.0**

## Overview

A reusable info card shown on each section's landing page that tells users what they can and can't do, and which roles have elevated access. Helps volunteers understand their permissions without asking a coordinator.

## Trigger

An info icon button (`fa-circle-info`) in each section's top button row. Clicking opens a Bootstrap modal with the access matrix table. No page navigation.

## Component

`AccessMatrixViewComponent` — a single reusable ViewComponent.

**Usage in views:**
```html
@await Component.InvokeAsync("AccessMatrix", new { section = "Shifts" })
```

Outputs two things:
1. The trigger button (an `<a>` styled as `btn btn-outline-secondary btn-sm` with `fa-circle-info` icon) — placed inline in the top button row
2. A hidden Bootstrap modal with the matrix table

## Data Structure

Static definitions in `AccessMatrixDefinitions.cs` (in `Humans.Web/Models/`). A dictionary keyed by section name. No database involvement.

```csharp
public static class AccessMatrixDefinitions
{
    public static readonly Dictionary<string, AccessMatrixData> Sections = new() { ... };
}

public class AccessMatrixData
{
    public string SectionName { get; init; }
    public List<string> Roles { get; init; }          // Column headers
    public List<AccessMatrixFeature> Features { get; init; }  // Rows
}

public class AccessMatrixFeature
{
    public string Name { get; init; }                  // Row label
    public Dictionary<string, AccessLevel> RoleAccess { get; init; }
}

public enum AccessLevel
{
    Allowed,    // Green checkmark
    Limited,    // Yellow/amber partial icon
    Denied      // Lock icon (muted)
}
```

## Sections & Matrices

Admin role is excluded from all matrices (can do everything everywhere). "Volunteer" below means any active member (not a formal role — it's the baseline). Admin-only sections (Admin tools) are excluded since the matrix would have no non-Admin columns.

### Shifts
**Roles:** Volunteer, Coordinator, NoInfoAdmin, VolunteerCoordinator

| Feature | Volunteer | Coordinator | NoInfoAdmin | VolunteerCoordinator |
|---------|-----------|-------------|-------------|---------------------|
| Browse shifts | Allowed | Allowed | Allowed | Allowed |
| Sign up for shifts | Allowed | Allowed | Allowed | Allowed |
| My Shifts & availability | Allowed | Allowed | Allowed | Allowed |
| Create/edit rotas & shifts | Denied | Allowed | Denied | Allowed |
| Approve/refuse signups | Denied | Allowed | Allowed | Allowed |
| Voluntell | Denied | Allowed | Allowed | Allowed |
| Staffing dashboard | Denied | Denied | Allowed | Allowed |

### Teams
**Roles:** Volunteer, Coordinator, Board, TeamsAdmin

| Feature | Volunteer | Coordinator | Board | TeamsAdmin |
|---------|-----------|-------------|-------|-----------|
| View teams & join | Allowed | Allowed | Allowed | Allowed |
| View team details | Allowed | Allowed | Allowed | Allowed |
| Manage members | Denied | Allowed | Denied | Allowed |
| Manage roles | Denied | Allowed | Denied | Allowed |
| Create/delete teams | Denied | Denied | Allowed | Allowed |
| Google resource sync | Denied | Denied | Denied | Limited |

### Camps
**Roles:** Volunteer, Camp Lead, CampAdmin

| Feature | Volunteer | Camp Lead | CampAdmin |
|---------|-----------|-----------|-----------|
| Browse camps | Allowed | Allowed | Allowed |
| Register a camp | Allowed | Allowed | Allowed |
| Edit own camp | Denied | Allowed | Allowed |
| Approve/reject camps | Denied | Denied | Allowed |
| Camp settings | Denied | Denied | Allowed |

### Governance
**Roles:** Volunteer, Board

| Feature | Volunteer | Board |
|---------|-----------|-------|
| View estatutos | Allowed | Allowed |
| Apply for tier | Allowed | Allowed |
| View applications | Limited | Allowed |
| Vote on applications | Denied | Allowed |

### Onboarding Review
**Roles:** ConsentCoordinator, VolunteerCoordinator, Board

| Feature | ConsentCoordinator | VolunteerCoordinator | Board |
|---------|-------------------|---------------------|-------|
| View onboarding queue | Allowed | Allowed | Allowed |
| Clear consent checks | Allowed | Denied | Allowed |
| Flag / reject signup | Allowed | Denied | Allowed |
| Board voting | Denied | Denied | Allowed |

### Board Dashboard
**Roles:** Board

| Feature | Board |
|---------|-------|
| Dashboard & stats | Allowed |
| Audit log | Allowed |
| Member data export | Allowed |

### Tickets
**Roles:** Board, TicketAdmin

| Feature | Board | TicketAdmin |
|---------|-------|-------------|
| View tickets & orders | Allowed | Allowed |
| Sync operations | Denied | Allowed |
| Discount codes | Denied | Allowed |

## Visual Design

Matches the existing screenshot style:
- Warm/cream card background (consistent with site theme)
- Clean table with role names as column headers
- Access levels rendered as icons:
  - **Allowed:** Green checkmark (`fa-circle-check`, text-success)
  - **Limited:** Amber/yellow half-circle (`fa-circle-half-stroke`, text-warning)
  - **Denied:** Lock icon (`fa-lock`, text-muted)
- Card header: "Access Matrix" with section name
- Modal size: `modal-lg` for sections with 3+ role columns, default for fewer

## Files

| File | Purpose |
|------|---------|
| `src/Humans.Web/Models/AccessMatrixDefinitions.cs` | Static matrix data for all sections |
| `src/Humans.Web/ViewComponents/AccessMatrixViewComponent.cs` | ViewComponent logic |
| `src/Humans.Web/Views/Shared/Components/AccessMatrix/Default.cshtml` | Modal + trigger button template |

## Integration Points

Each section's index view adds the component to its top button row:
- `Views/Shifts/Index.cshtml`
- `Views/Team/Index.cshtml`
- `Views/Camp/Index.cshtml`
- `Views/Governance/Index.cshtml`
- `Views/OnboardingReview/Index.cshtml`
- `Views/Board/Index.cshtml`
- `Views/Ticket/Index.cshtml`

## Maintenance

Weekly verification task: confirm the hardcoded matrices match actual controller auth checks. Added to `.claude/MAINTENANCE_LOG.md` as a weekly recurring task.
