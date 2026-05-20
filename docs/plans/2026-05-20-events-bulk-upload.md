# Plan — Events Bulk Upload for Barrio Leads

**Branch:** `feat/events-host-and-unified-submissions`
**Guide:** `docs/features/26-events.md` (US-26.10)

## Goals

Allow Camp Leads and Workshop Leads to download their barrio's current events as a CSV, add new rows or edit existing ones, and upload the file back — without submitting events one by one.

The download link and upload form live **inline in each barrio block on `/Events/MySubmissions`** — no separate bulk upload page. Errors are shown in TempData, rendered back in the barrio block after redirect.

Scope decisions (confirmed): no deletion or withdrawal via bulk upload (separate feature); submission window enforced same as single-event form; not for admins bypassing the window.

---

## Part A — View models

### A1. `src/Humans.Web/Models/Events/BarrioEventViewModels.cs`

Add `BulkRowError` (used in TempData to carry per-row errors back to MySubmissions after a failed upload):

```csharp
public class BulkRowError
{
    public int RowNumber { get; set; }
    public string Title { get; set; } = "";
    public List<string> Errors { get; set; } = [];
}
```

The existing `MySubmissionsViewModel` (or its barrio block sub-model) gains a `List<BulkRowError> BulkUploadErrors` property populated from TempData after a failed upload POST.

---

## Part B — Controller actions

### B1. `src/Humans.Web/Controllers/EventsController.cs`

Add three actions and two private helpers.

#### `BulkUploadTemplate` (GET)

```csharp
[HttpGet("Barrio/{slug}/BulkUpload/Template")]
public async Task<IActionResult> BulkUploadTemplate(string slug)
```

- Call `ResolveCampEventManagementAsync(slug)` — 403 if fails.
- Call `GetCampSubmissionsAsync(camp.Id)` — fetch all non-Withdrawn events.
- Call `GetActiveCategoriesAsync()` — for the comment block listing valid categories.
- Build CSV in memory:
  - Prepend `#`-comment lines (format hints, valid categories, Id rule, Barrio/Status are read-only).
  - Header row: `"Id","Barrio","Status","Title","Description","Category","Date","StartTime","DurationMinutes","LocationNote","Host","IsRecurring","RecurrenceDays","PriorityRank"`
  - One data row per non-Withdrawn event. `Date` from `StartAt` converted to local date via `tz`. `StartTime` as `HH:mm`. `RecurrenceDays` as space-separated day names (`Mon Tue Fri`).
- Return `File(bytes, "text/csv", $"{slug}-events.csv")`.

#### `BulkUploadImport` (POST)

```csharp
[HttpPost("Barrio/{slug}/BulkUpload")]
public async Task<IActionResult> BulkUploadImport(string slug, IFormFile file)
```

- Call `ResolveCampEventManagementAsync(slug)` — 403 if fails.
- Enforce submission window (`SubmissionOpenAt` / `SubmissionCloseAt`) — redirect or error if closed.
- Parse: `ParseCsvRows(file)` — returns `List<BulkCsvRow>` (or error list).
- Validate: `ValidateBulkRows(rows, camp, categories, existingEvents)` — returns `List<BulkRowError>`.
- If any errors → store errors in `TempData["BulkErrors_{slug}"]` (serialized JSON), redirect to `MySubmissions`. The MySubmissions action reads TempData and injects errors into the matching barrio block.
- For each row:
  - Empty Id → `SubmitEventAsync(newEvent)`.
  - Non-empty Id, fields unchanged → skip.
  - Non-empty Id, fields changed, status Draft → `SubmitEventAsync(updatedEvent)`.
  - Non-empty Id, fields changed, other status → `UpdateAndResubmitAsync(updatedEvent)`.
- Redirect to `MySubmissions`.

#### `ParseCsvRows` (private)

- Skip lines starting with `#`.
- Skip blank lines.
- Parse header row (validate expected columns present).
- Parse each data row into a `BulkCsvRow` record (raw strings, no validation yet).
- Return rows with 1-based row numbers for error reporting.

#### `ValidateBulkRows` (private)

Per-row checks:
- Required fields present (Title, Description, Category, Date, StartTime, DurationMinutes, PriorityRank).
- `Title` ≤ 80, `Description` ≤ 450, `LocationNote` ≤ 120, `Host` ≤ 40.
- `DurationMinutes` integer 15–480, divisible by 15.
- `PriorityRank` integer 1–100.
- `Date` parses to `LocalDate` (`yyyy-MM-dd`); `StartTime` parses to `LocalTime` (`HH:mm`).
- `Category` matches an active category (case-insensitive by name).
- If `Id` non-empty: event exists for this camp; status is not `Withdrawn`.

Collect all errors across all rows. Return full list (never short-circuit).

---

## Part C — View

### C1. `src/Humans.Web/Views/Events/MySubmissions.cshtml` — barrio block additions

In each barrio block, after the existing event list:

- Download link: `<a href="/Events/Barrio/{slug}/BulkUpload/Template">Download events CSV</a>`.
- Upload form: `enctype="multipart/form-data"`, single file input (`.csv`), POST to `/Events/Barrio/{slug}/BulkUpload`, submit button.
- If `Model.BarrioBlocks[i].BulkUploadErrors` is non-empty: render an error table with columns Row, Title, Errors. One row per `BulkRowError`, errors as a `<ul>` in the Errors cell.

---

## Verification

1. `dotnet build Humans.slnx -v quiet` — 0 errors.
2. `dotnet test Humans.slnx -v quiet` — all pass.
3. Manual:
   - Camp lead opens `/Events/MySubmissions` → barrio block shows download link and file upload form.
   - Download template → CSV has correct columns, existing events populated, Withdrawn events absent, comment lines at top.
   - Upload CSV with one new row (empty Id) → event appears in MySubmissions as Pending.
   - Upload same CSV again (now has an Id, fields unchanged) → event not duplicated, status preserved.
   - Upload CSV with a changed field on an existing event → event updated and re-queued for moderation.
   - Upload CSV with an invalid row (bad category, missing title, etc.) → error table shown, nothing saved.
   - Attempt upload with submission window closed → rejected.
   - Non-lead user attempts upload → 403.
