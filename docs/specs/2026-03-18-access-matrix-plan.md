# Access Matrix Card Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a reusable access matrix info card to each section of the site, showing users what they can/can't do and which roles have elevated access.

**Architecture:** A single `AccessMatrixViewComponent` renders a Bootstrap modal with a permission table. Static data definitions in `AccessMatrixDefinitions.cs` define the matrix per section. Each section's landing page adds the component inline — sections with a button row get it in the button row, others get it below the title.

**Tech Stack:** ASP.NET Core 10 ViewComponents, Bootstrap 5 modals, Font Awesome 6 icons

**Spec:** `docs/specs/2026-03-18-access-matrix-card-design.md`

---

## Chunk 1: Component + Data Model

### Task 1: Create AccessLevel enum and data model

**Files:**
- Create: `src/Humans.Web/Models/AccessMatrixDefinitions.cs`

- [ ] **Step 1: Create the data model and enum**

```csharp
namespace Humans.Web.Models;

public enum AccessLevel
{
    Allowed,
    Limited,
    Denied
}

public class AccessMatrixData
{
    public required string SectionName { get; init; }
    public required List<string> Roles { get; init; }
    public required List<AccessMatrixFeature> Features { get; init; }
}

public class AccessMatrixFeature
{
    public required string Name { get; init; }
    public required Dictionary<string, AccessLevel> RoleAccess { get; init; }
}
```

- [ ] **Step 2: Add static definitions for all sections**

Add `public static class AccessMatrixDefinitions` with a `Dictionary<string, AccessMatrixData>` containing entries for: Shifts, Teams, Camps, Governance, OnboardingReview, Board, Tickets. Use the exact matrices from the design spec.

- [ ] **Step 3: Build**

Run: `dotnet build Humans.slnx`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/Models/AccessMatrixDefinitions.cs
git commit -m "feat: add AccessMatrixDefinitions with per-section permission data"
```

---

### Task 2: Create AccessMatrixViewComponent

**Files:**
- Create: `src/Humans.Web/ViewComponents/AccessMatrixViewComponent.cs`

- [ ] **Step 1: Create the ViewComponent**

```csharp
using Humans.Web.Models;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.ViewComponents;

public class AccessMatrixViewComponent : ViewComponent
{
    public IViewComponentResult Invoke(string section)
    {
        if (!AccessMatrixDefinitions.Sections.TryGetValue(section, out var data))
            return Content(string.Empty);

        return View(data);
    }
}
```

No async needed — data is static. No DI needed.

- [ ] **Step 2: Build**

Run: `dotnet build Humans.slnx`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/ViewComponents/AccessMatrixViewComponent.cs
git commit -m "feat: add AccessMatrixViewComponent"
```

---

### Task 3: Create the view template

**Files:**
- Create: `src/Humans.Web/Views/Shared/Components/AccessMatrix/Default.cshtml`

- [ ] **Step 1: Create the Razor view**

The view renders two things:
1. A trigger button (`btn btn-outline-secondary` with `fa-circle-info` icon) that opens the modal
2. A Bootstrap modal with the access matrix table

Use `modal-lg` when 3+ role columns, default otherwise. Table uses warm/cream card style matching the site theme.

Icon rendering per AccessLevel:
- `Allowed`: `<i class="fa-solid fa-circle-check text-success"></i>`
- `Limited`: `<i class="fa-solid fa-circle-half-stroke text-warning"></i>`
- `Denied`: `<i class="fa-solid fa-lock text-muted"></i>`

The modal ID should be unique per section: `accessMatrix-@Model.SectionName`.

- [ ] **Step 2: Build**

Run: `dotnet build Humans.slnx`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/Views/Shared/Components/AccessMatrix/
git commit -m "feat: add AccessMatrix view template with modal and trigger button"
```

---

## Chunk 2: Integration into Section Pages

### Task 4: Add to Shifts section

**Files:**
- Modify: `src/Humans.Web/Views/Shifts/Index.cshtml`

- [ ] **Step 1: Add component to the button row**

In the `<div class="d-flex gap-2">` button group (after the `<h2>` title), add before the first conditional button:

```html
<vc:access-matrix section="Shifts" />
```

- [ ] **Step 2: Build and verify**

Run: `dotnet build Humans.slnx`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/Views/Shifts/Index.cshtml
git commit -m "feat: add access matrix to Shifts section"
```

---

### Task 5: Add to Teams section

**Files:**
- Modify: `src/Humans.Web/Views/Team/Index.cshtml`

- [ ] **Step 1: Add component to the button row**

In the `<div class="d-flex gap-2">` button group, add at the start:

```html
<vc:access-matrix section="Teams" />
```

- [ ] **Step 2: Commit**

```bash
git add src/Humans.Web/Views/Team/Index.cshtml
git commit -m "feat: add access matrix to Teams section"
```

---

### Task 6: Add to Camps section

**Files:**
- Modify: `src/Humans.Web/Views/Camp/Index.cshtml`

- [ ] **Step 1: Add component to the button row**

In the `<div class="d-flex gap-2">` button group, add at the start:

```html
<vc:access-matrix section="Camps" />
```

- [ ] **Step 2: Commit**

```bash
git add src/Humans.Web/Views/Camp/Index.cshtml
git commit -m "feat: add access matrix to Camps section"
```

---

### Task 7: Add to Governance section

**Files:**
- Modify: `src/Humans.Web/Views/Governance/Index.cshtml`

Governance has no button row — it uses a card-based layout with `<h1 class="mb-4">` title. Add the component inline after the title.

- [ ] **Step 1: Add component after the title**

After the `<h1>` tag, add:

```html
<div class="mb-3">
    <vc:access-matrix section="Governance" />
</div>
```

- [ ] **Step 2: Commit**

```bash
git add src/Humans.Web/Views/Governance/Index.cshtml
git commit -m "feat: add access matrix to Governance section"
```

---

### Task 8: Add to Onboarding Review section

**Files:**
- Modify: `src/Humans.Web/Views/OnboardingReview/Index.cshtml`

No button row — title then alerts. Add after title.

- [ ] **Step 1: Add component after the title**

After the `<h1>` tag and before `<vc:temp-data-alerts />`, add:

```html
<div class="mb-3">
    <vc:access-matrix section="OnboardingReview" />
</div>
```

- [ ] **Step 2: Commit**

```bash
git add src/Humans.Web/Views/OnboardingReview/Index.cshtml
git commit -m "feat: add access matrix to Onboarding Review section"
```

---

### Task 9: Add to Board section

**Files:**
- Modify: `src/Humans.Web/Views/Board/Index.cshtml`

No button row — dashboard with `<h1>` then card grid. Add after title.

- [ ] **Step 1: Add component after the title**

After the `<h1>Board Dashboard</h1>` tag, add:

```html
<div class="mb-3">
    <vc:access-matrix section="Board" />
</div>
```

- [ ] **Step 2: Commit**

```bash
git add src/Humans.Web/Views/Board/Index.cshtml
git commit -m "feat: add access matrix to Board section"
```

---

### Task 10: Add to Tickets section

**Files:**
- Modify: `src/Humans.Web/Views/Ticket/Index.cshtml`

Uses `container-fluid` with `<h1>` then partial nav. Add after title.

- [ ] **Step 1: Add component after the title**

After `<h1 class="mt-4">Tickets</h1>`, add:

```html
<div class="mb-3">
    <vc:access-matrix section="Tickets" />
</div>
```

- [ ] **Step 2: Commit**

```bash
git add src/Humans.Web/Views/Ticket/Index.cshtml
git commit -m "feat: add access matrix to Tickets section"
```

---

### Task 11: Final build and test

- [ ] **Step 1: Full build**

Run: `dotnet build Humans.slnx`
Expected: Build succeeded, 0 errors

- [ ] **Step 2: Full test suite**

Run: `dotnet test Humans.slnx`
Expected: All tests pass

- [ ] **Step 3: Commit all remaining changes (if any)**

---

## Chunk 3: Maintenance Setup

### Task 12: Add weekly verification to maintenance log

**Files:**
- Modify: `.claude/MAINTENANCE_LOG.md`

- [ ] **Step 1: Add access matrix verification as a weekly task**

Add entry: "Access Matrix Verification — compare hardcoded matrices in `AccessMatrixDefinitions.cs` against actual controller auth checks. Verify each feature/role combination matches the code."

Schedule: Weekly, next due: 2026-03-25.

- [ ] **Step 2: Commit**

```bash
git add .claude/MAINTENANCE_LOG.md
git commit -m "chore: add weekly access matrix verification to maintenance log"
```
