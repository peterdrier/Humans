---
name: nav-audit
description: "Audit site navigation — find dead-end features, missing backlinks, and poor discoverability. Produces an improvement list, no code changes."
argument-hint: "all | teams | admin | profile | consent | governance"
---

# Navigation & Discoverability Audit

Audit every feature to verify it's reachable through obvious UI paths. Produces an improvement list — does NOT make changes.

## Severity Levels

| Severity | Meaning |
|----------|---------|
| **SHOWSTOPPER** | Feature only reachable by typing the URL directly. |
| **POOR** | Reachable but path is non-obvious, buried, or link text is unclear. |
| **MISSING BACKLINK** | No link from a listing/parent page to its detail/action page. |
| **SUGGESTION** | Works but could be improved (shortcut, breadcrumb, contextual button). |

## Authorization Awareness

Audit each route for the role that should use it. Hidden admin features are correct, not bugs.

| Role | Sees |
|------|------|
| Unauthenticated | Public pages (home, login) |
| Authenticated (no approval) | Membership gate (profile setup, consent) |
| Active member | Full member experience |
| Lead | Team admin for their teams |
| Board | Admin panel subset, member approval |
| Admin | Full admin panel, configuration |

## Process

### Step 1: Build route inventory

Scan for all controller actions and Razor pages — URL, HTTP method, required auth, purpose.

Sources:
- `src/Humans.Web/Controllers/*.cs` — `[Route]`, `[HttpGet]`, `[HttpPost]`, `[Authorize]` attributes
- `src/Humans.Web/Pages/**/*.cshtml` — Razor pages
- `src/Humans.Web/Views/**/*.cshtml` — MVC views

### Step 2: Build navigation map

For each route, find inbound links in:
- **Layouts/nav**: `_Layout.cshtml`, `_AdminLayout.cshtml`, sidebar partials
- **Page links**: `<a>`, `asp-action`, `asp-controller`, `asp-page` tag helpers
- **Contextual actions**: edit/delete/view buttons in list items
- **Dashboard widgets**: admin dashboard cards

### Step 3: Analyze gaps

For each route determine:
1. Is it linked from its natural parent? (detail ← list, edit ← view, admin sub-page ← admin nav)
2. Is it reachable within 1-2 clicks for the target role?
3. Zero inbound links → SHOWSTOPPER; linked only from non-obvious places → POOR

### Step 4: Check action flows

Verify multi-step workflows have clear navigation: form submit lands somewhere useful, list action buttons are visible, success pages link back to the relevant list.

### Step 5: Produce report

Output sections in order: **Route Inventory** (totals by role), then four issue tables (SHOWSTOPPERS / POOR Discoverability / MISSING BACKLINKS / SUGGESTIONS), each with columns: Route | Feature | Problem/Current Path | Suggested Fix. Close with a counts summary.

### Step 6: Wait for discussion

Present the report. Do NOT make code changes until the user approves specific items.

## Scope Control

If `$ARGUMENTS` specifies a section, audit only that area:
- `all` (default) — entire site
- `teams` — team browsing, joining, team admin
- `admin` — admin panel navigation
- `profile` — profile, email, privacy, contact fields
- `consent` — legal documents and consent flow
- `governance` — asociado application flow
