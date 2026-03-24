# Session 3: Shift UI Polish + Public Legal Section

**Date:** 2026-03-23
**Issues:** #184, #180, #176, #194

## Batch 5: Shift UI Polish

### #184 — Text/Label Cleanup

#### 1. "Browse Shifts" → "Browse Volunteer Options"

Four locations:
- `Views/Shifts/Index.cshtml:37` — page `<h2>` heading
- `Views/Shifts/Mine.cshtml:39` — link back to browse
- `Views/Home/_ShiftCards.cshtml:47` — dashboard link button
- `Views/Vol/Register.cshtml:51` — onboarding link

#### 2. "Sign Up for Range" → "Sign up for these dates"

- `Views/Shifts/Index.cshtml:147` — range signup form button

#### 3. Remove duplicate shift link from Team Management card

- Delete lines 334-339 in `Views/Team/Details.cshtml` (the `Nav_Shifts` link in Team Management card)
- The Shifts summary card (`_ShiftsSummaryCard.cshtml`) already has "Manage Shifts" — this is the single entry point

#### 4. Always show Shifts card for departments

- `Views/Team/Details.cshtml:299` — currently gated on `Model.ShiftsSummary != null`
- Change: always render the Shifts card for department teams (parent teams that aren't system teams)
- When no `ShiftsSummary` data exists, show just the "Manage Shifts" button without the progress bar
- Requires the controller to always provide a `ShiftsUrl` even when there are no shifts yet
- Controller must always populate a `ShiftsSummaryCardViewModel` for departments — with zero counts if no shifts exist, plus the URL and `CanManageShifts` flag

#### Nav_Shifts resource key

`Nav_Shifts` currently resolves to "Volunteer" in all locales. This is used in:
- Main navbar (`_Layout.cshtml:54`) — keep as-is, "Volunteer" is correct for the public nav link
- Team Management card (`Details.cshtml:337`) — being removed entirely

No resource key changes needed.

---

### #180 — Clarify Rota Separation (Build/Event/Strike)

#### Period section headers

Group rotas by period within each department card. Add bold section headers before each period group:
- **"Set-up"** (Build period)
- **"Event"** (Event period)
- **"Strike"** (Strike period)

With a horizontal rule or visual divider between period groups.

#### Period-labeled date pickers

Relabel the range signup form button per period:
- Build rotas: **"Sign up for set-up dates"**
- Strike rotas: **"Sign up for strike dates"**

#### Empty period note

If a period group has rotas but all shifts are full, show: "All [period] shifts are full."

#### Implementation

Changes in `Views/Shifts/Index.cshtml`:
- Group `dept.Rotas` by `Rota.Period` before iterating
- Add period header `<h5>` with the period display name and a `<hr>` divider
- Modify the range signup button text to include the period name

---

### #176 — Batch Voluntell (Range Assignment)

#### New service method: `VoluntellRangeAsync`

In `ShiftSignupService.cs`:

```csharp
public async Task<SignupResult> VoluntellRangeAsync(
    Guid userId, Guid rotaId, int startDayOffset, int endDayOffset, Guid enrollerUserId)
```

Logic combines `SignUpRangeAsync` (date range iteration, `SignupBlockId` for grouped bail) with `VoluntellAsync` (confirmed signup, `EnrolledByUserId`, no approval needed):
- Iterate all-day shifts in the rota between `startDayOffset` and `endDayOffset`
- Create confirmed signups with shared `SignupBlockId`
- Set `Enrolled = true` and `EnrolledByUserId = enrollerUserId`
- Skip shifts where user is already signed up (don't error, just skip)
- Check for overlap conflicts — same semantics as existing `VoluntellAsync` (error if user already has a confirmed/pending signup on the same shift)
- Return `SignupResult` with count of assigned shifts

#### New controller action: `VoluntellRange`

In `ShiftAdminController.cs`:

```csharp
[HttpPost("VoluntellRange")]
[ValidateAntiForgeryToken]
public async Task<IActionResult> VoluntellRange(string slug, Guid rotaId, int startDayOffset, int endDayOffset, Guid userId)
```

- Requires `ResolveDepartmentApprovalAsync` (same auth as single Voluntell)
- Validates rota belongs to team
- Calls `VoluntellRangeAsync`
- Redirects back to ShiftAdmin Index

#### Admin view UI

In `Views/ShiftAdmin/Index.cshtml`, for Build/Strike rotas only:
- Single form at the top of each rota section (above the shift table)
- Date range picker: start/end `<select>` dropdowns with available day offsets
- Volunteer search field: text input with JS-driven search (reusing existing `SearchVolunteers` pattern)
- Submit button: "Assign to Range"
- Only visible when `Model.CanApproveSignups`

---

## Batch 9: Public Legal Section (#194)

### New service: `ILegalDocumentService` / `LegalDocumentService`

**Interface** in `Humans.Application`:

```csharp
public interface ILegalDocumentService
{
    IReadOnlyList<LegalDocumentDefinition> GetAvailableDocuments();
    Task<Dictionary<string, string>> GetDocumentContentAsync(string slug);
}

public record LegalDocumentDefinition(string Slug, string DisplayName, string RepoFolder, string FilePrefix);
```

**Implementation** in `Humans.Infrastructure`:

Hardcoded document definitions:
```csharp
private static readonly LegalDocumentDefinition[] Documents =
[
    new("statutes", "Statutes", "Estatutos", "ESTATUTOS"),
    // Future: new("privacy-policy", "Privacy Policy", "Privacy", "PRIVACY"),
];
```

**Methods:**
- `GetAvailableDocuments()` — returns the static list
- `GetDocumentContentAsync(string slug)` — looks up definition by slug, fetches from GitHub using `RepoFolder` + `FilePrefix`, caches per-document with key `Legal:{slug}`, 1-hour TTL

**GitHub fetching:** Generalized from `GovernanceController.FetchStatutesContentAsync()`:
- Uses `GitHubSettings` for owner/repo/token/branch
- Fetches all `.md` files in `RepoFolder` matching `FilePrefix*`
- Extracts language code from filename via existing `LanguageFilePattern` regex
- Returns `Dictionary<string, string>` (lang code → markdown content)

### New controller: `LegalController`

```csharp
[Route("Legal")]
[AllowAnonymous]
public class LegalController : Controller
```

- Add `"Legal"` to `MembershipRequiredFilter` exempt list
- Single action: `GET /Legal/{slug?}` defaults to first document; returns 404 for invalid slugs

**View model:**

```csharp
public class LegalPageViewModel
{
    public IReadOnlyList<LegalDocumentDefinition> AllDocuments { get; init; }
    public string CurrentSlug { get; init; }
    public string CurrentDocumentName { get; init; }
    public TabbedMarkdownDocumentsViewModel DocumentContent { get; init; }
}
```

**View** (`Views/Legal/Index.cshtml`):
- Pill nav across top: `<ul class="nav nav-pills mb-4">` listing all documents, active pill for current slug
- Below: full-page `_TabbedMarkdownDocuments` partial with no height constraint (override `ContentStyle` to remove `max-height: 500px`)
- Page title: current document name

### Navigation changes

**Logged out** (`_Layout.cshtml`):
- Add "Legal" link in top navbar, after Teams, before any auth-gated items
- Visible to all visitors

**Logged in** (`_LoginPartial.cshtml`):
- Add "Legal" link in profile dropdown menu
- Icon: `fa-scale-balanced`
- Placed after Governance link (or after Consents if user doesn't see Governance)

### Governance page refactoring

- `GovernanceController` injects `ILegalDocumentService` instead of `IMemoryCache` + `GitHubClient`
- `Index` action calls `_legalDocService.GetDocumentContentAsync("statutes")`
- Delete private methods: `GetStatutesContentAsync()`, `FetchStatutesContentAsync()`
- Delete `StatutesCacheTtl` constant and `LanguageFilePattern` regex (moved to service)
- View unchanged — still embeds statutes in its existing card layout with `max-height` scroll

### Adding new documents

To add a new legal document (e.g., Privacy Policy):
1. Add markdown files to `nobodies-collective/legal` repo (e.g., `Privacy/PRIVACY.md`, `Privacy/PRIVACY-en.md`)
2. Add entry to `LegalDocumentService.Documents` array
3. No other code changes — pill nav auto-populates, routing handles it

---

## Future work

- #198: Refactor shift views into reusable partials (low priority)
