# Day-Off Redesign

Re-introduces the "day off" concept to volunteer tracking after the first iteration was removed. Coord-only annotation marking specific days a volunteer is known to be unavailable, so the gap-detector stops chasing them and the row stops looking under-staffed for that day.

## Background

A previous iteration shipped a "block / unblock day" feature with two write paths (coord-side cell toggle + volunteer self-service multi-select on `/Shifts/Mine`) and stored offsets as a `List<int>` jsonb column on `volunteer_build_statuses`. It was removed in May 2026 because:

- The cell-state precedence silently shadowed the action: marking a day "blocked" on a cell already covered by camp-setup or a confirmed signup did update the data but never changed the visual, leaving the coord confused.
- The self-service surface was the wrong product call — in practice 90% of cases come up via conversation; a system for users to assign their own days off creates more drift than it prevents.
- The wording ("block / unblock") didn't match how coords talk about it.

The removal was scoped specifically (one branch commit) so the underlying coordination need stays open: a coord still wants to ack "Maria has a doctor appointment Tuesday — don't chase her, don't flag a coverage gap." This spec re-introduces just that, narrower.

## Goals

- A coord can mark a single build-period day as "off" for a specific volunteer, optionally with a short reason ("doctor", "city visit").
- The marked day renders distinctly on the volunteer-tracking heatmap and does **not** count toward the row's gap count.
- A second coord (or the same one later) can clear the mark.
- All writes go through the audit log.

## Non-Goals

- No volunteer self-service surface. `/Shifts/Mine` stays as it is post-removal.
- No span/range UI. Multi-day off = multiple clicks.
- No interaction with the declared-but-unbooked cohort.
- No notification to the volunteer when they're marked off.
- No "expected back" date semantics distinct from the day-off period.

## Constraints

- Writes only by `VolunteerTrackingWrite` policy holders (Admin, VolunteerCoordinator).
- Storage stays as a jsonb collection on the existing `volunteer_build_statuses` row (Approach 1 from brainstorming) — no new table.
- Audit enum is positional; the three reserved values from the removal (`VolunteerDayBlocked`, `VolunteerDayUnblocked`, `VolunteerOwnBlockedDaysSaved`) stay where they are. New audit actions are appended at the end.
- All cross-section reads stay through services (no new EF cross-section navigations).

## Data Model

### Entity changes

`Humans.Domain.Entities.VolunteerBuildStatus` gains one collection property:

```csharp
public List<DayOffEntry> DayOffs { get; set; } = new();
```

with a new domain record:

```csharp
public sealed record DayOffEntry(
    int DayOffset,        // negative offset relative to GateOpeningDate, [BuildStartOffset, 0)
    string? Reason,       // trimmed; null when blank; max 200 chars
    Guid MarkedByUserId,  // coord who created/last-replaced this entry
    Instant MarkedAt);    // timestamp of last write for this entry
```

Existing fields on `VolunteerBuildStatus` (`BarrioSetupStartDate`, `Notes`, `SetByUserId`, `SetAt`) are unchanged — they continue to govern only camp-setup state.

### Storage

`DayOffs` maps to a single new jsonb column `DayOffs`. Existing rows get default `'[]'::jsonb` so no backfill is needed. The EF configuration mirrors the pattern used by `GeneralAvailability.AvailableDayOffsets` — `HasColumnType("jsonb")` plus a `ValueComparer<List<DayOffEntry>>` (`SequenceEqual` / element-wise hash / `ToList()` snapshot) so EF detects in-place mutations.

The column is non-null with default `[]` so no code path needs to handle a null collection.

### Per-entry invariants

Enforced in the service layer (not the database):

| Rule | Error key on violation |
|---|---|
| `BuildStartOffset ≤ DayOffset < 0` | `VolTrack_Err_DayOffOutsideBuild` |
| Volunteer has no Confirmed/Pending Build signup on that day | `VolTrack_Err_DayOffWithSignups` |
| Day does not fall within the volunteer's camp-setup span (`d ≥ setupOffset`) | `VolTrack_Err_DayOffDuringCampSetup` |
| At most one entry per `DayOffset` per `(UserId, EventSettingsId)` row | (repository normalizes; replaces existing entry for same day) |
| Reason: trimmed; whitespace-only → null; longer than 200 chars → truncated to 200 | (no error; silent normalization) |

The signup-overlap rule means the coord must bail any pre-existing signups on the day before they can ack it as off. The camp-setup-overlap rule means a day inside camp-setup can't be a day off (the volunteer is already on-site for build; chasing isn't a coverage problem).

The unique-per-day invariant ensures the cell-state computation only ever sees one entry per cell.

## Application Layer

### `IVolunteerTrackingService` — new methods

```csharp
/// Coordinator path. Caller has already authorized.
Task<SetDayOffResult> SetDayOffAsync(
    Guid targetUserId, int dayOffset, string? reason,
    Guid coordinatorUserId, CancellationToken ct = default);

/// Coordinator path. Caller has already authorized.
/// Idempotent: no-op if no entry exists for (userId, dayOffset).
Task<ClearDayOffResult> ClearDayOffAsync(
    Guid targetUserId, int dayOffset,
    Guid coordinatorUserId, CancellationToken ct = default);
```

with result records:

```csharp
public sealed record SetDayOffResult(bool Ok, string? ErrorMessageKey);
public sealed record ClearDayOffResult(bool Ok, bool Removed);
```

Service implementation:

- Loads active event via the existing `GetActiveEventSettingsAsync`. Throws if absent (matches existing camp-setup behaviour).
- Validates `dayOffset` against `BuildStartOffset` and 0.
- For `SetDayOffAsync`: queries `GetEligibleBuildSignupsAsync(es.Id)` and rejects if `(userId, dayOffset)` has a Confirmed or Pending signup. Loads the existing build-status row and rejects if `BarrioSetupStartDate` is set with `setupOffset ≤ dayOffset`.
- Trims `reason`, coerces blank → `null`, caps at 200 chars.
- Delegates the write to the repository, passing a fully-constructed `DayOffEntry` (timestamp from `IClock`). Service owns the clock concern; repo persists what it's given.

`ClearDayOffAsync` does the same active-event check, then delegates a remove-by-(userId, dayOffset) call to the repo. The bool return reflects whether something was actually removed (drives audit emission in the controller).

### `IVolunteerTrackingRepository` — new methods

```csharp
Task UpsertDayOffAsync(
    Guid userId, Guid eventSettingsId,
    DayOffEntry entry, CancellationToken ct = default);

Task<bool> RemoveDayOffAsync(
    Guid userId, Guid eventSettingsId, int dayOffset,
    CancellationToken ct = default);
```

Repository implementation:

- `UpsertDayOffAsync`: loads the row (creates one with empty `DayOffs` if absent), removes any existing entry with the same `DayOffset`, appends the new entry, sorts by `DayOffset` ascending, saves.
- `RemoveDayOffAsync`: loads the row; if absent, returns `false`. Otherwise removes the entry matching `DayOffset`, returns whether a removal happened, saves only if it did.

The sort-on-write means cell rendering can rely on ordered traversal without a per-request sort.

### Cell-state precedence

`VolunteerCellState` gains one value:

```csharp
DayOff,
```

In `VolunteerTrackingService.GetTrackingDataAsync`, the main-cohort cell-state branch becomes:

```
1. CampSetup        (d ≥ setupOffset)
2. Outside          (d < firstSignupDay  OR  d ≥ lastExpectedDay)
3. Confirmed/Pending (signup exists for that day)
4. DayOff           (entry exists in DayOffs for that day)
5. Gap              (otherwise)
```

By validation, branches 1, 3, and 4 are mutually exclusive in practice — but ordering DayOff above Gap is what makes the day not count as a gap. `GapCount` increments only on branch 5.

The unbooked cohort is unchanged. Day-offs apply only to the main cohort (volunteers with at least one Build signup); the unbooked cohort has no commitment to ack.

## Web Layer

### Controller actions

`VolunteerTrackingController` gains two actions parallel to `SetCampSetup` / `ClearCampSetup`:

```csharp
[HttpPost("SetDayOff")]
[Authorize(Policy = PolicyNames.VolunteerTrackingWrite)]
[ValidateAntiForgeryToken]
public async Task<IActionResult> SetDayOff(SetDayOffForm form, CancellationToken ct);

[HttpPost("ClearDayOff")]
[Authorize(Policy = PolicyNames.VolunteerTrackingWrite)]
[ValidateAntiForgeryToken]
public async Task<IActionResult> ClearDayOff(ClearDayOffForm form, CancellationToken ct);
```

Form models:

```csharp
public sealed class SetDayOffForm
{
    public Guid UserId { get; set; }
    public int DayOffset { get; set; }
    [StringLength(200)] public string? Reason { get; set; }
}

public sealed class ClearDayOffForm
{
    public Guid UserId { get; set; }
    public int DayOffset { get; set; }
}
```

Both actions:
- Resolve the current user via `GetCurrentUserAsync`; `Forbid` if absent.
- Call the service.
- On `Ok = false` from `SetDayOffAsync`: surface the error key as a TempData error and `BadRequest` (no audit).
- On success (or `Removed = true`): write one audit row, set success TempData, redirect to `Index`.
- For `ClearDayOff` with `Removed = false`: redirect to `Index` with no audit and no success message (coord clicked clear on something that wasn't there; nothing changed).

### Audit emission

Two new `AuditAction` values appended at the end of the enum:

```csharp
VolunteerDayOffMarked,
VolunteerDayOffCleared,
```

(The three reserved values from the removed iteration — `VolunteerDayBlocked`, `VolunteerDayUnblocked`, `VolunteerOwnBlockedDaysSaved` — stay where they are with the existing reservation comment. They remain unused by the new code; their only purpose is resolving historical audit_log rows on dev DBs.)

Audit row description format:

- Marked: `DayOffset=-3; reason="doctor"` (or `reason=—` when blank).
- Cleared: `DayOffset=-3; cleared` (the prior reason is in earlier audit rows for the same entry).

`EntityType = nameof(VolunteerBuildStatus)`, `EntityId = targetUserId`, `ActorUserId = current.Id`. Same shape as `VolunteerCampSetupSet`.

### Popover view (`_VolunteerHeatmap.cshtml`)

The cell popover gains a Day-off section below the camp-setup section. It is **server-rendered conditionally** based on the cell's current state:

| Cell state | Rendered |
|---|---|
| DayOff (entry exists for this day) | A small caption showing the reason (or "no reason given"), then a `Cancel day off` button posting `ClearDayOff`. |
| Confirmed or Pending | A muted note: "Bail this signup before marking a day off." (no button) |
| CampSetup | A muted note: "Camp set-up is already covering this day." (no button) |
| Gap or Outside | A small `<input name="Reason" placeholder="Reason (optional)">` plus a `Mark day off` button posting `SetDayOff`. |

The conditional rendering means the coord visually sees what's possible before clicking. The server-side validation is the source of truth (defence in depth) but the user only hits its error path if the page state went stale between render and click — acceptable for a coord-only flow with low concurrency.

Buttons stack vertically inside the popover, separated by `<hr class="my-2" />`, matching the existing camp-setup pattern.

### Cell rendering

A new CSS class `vt-dayoff` lives in `Index.cshtml`'s `<style>` block:

```css
.vt-dayoff {
  background: #f8f9fa;
  background-image: repeating-linear-gradient(
    45deg,
    rgba(0, 0, 0, 0.18) 0,
    rgba(0, 0, 0, 0.18) 2px,
    transparent 2px,
    transparent 6px);
}
```

This is the "striped gray" option from the visual round of brainstorming. It reads as "we know about this, intentionally not-here" without competing with the green-confirmed or red-gap signals.

`CellClass`/`CellAria` switches in `_VolunteerHeatmap.cshtml` get one new arm each:
- `VolunteerCellState.DayOff => "vt-dayoff"`
- `VolunteerCellState.DayOff => Localizer["VolTrack_State_DayOff"].Value`

The unbooked cohort partial is left alone — `DayOff` doesn't appear there.

### Row-label badge

When a row has any day-off entries, the row label area (alongside the gap badge) gets a small badge:

```html
<span class="badge bg-secondary-subtle border" title="Wed 24 Jun — doctor; Fri 26 Jun">
  <i class="fa-solid fa-calendar-xmark me-1"></i>
  2 days off
</span>
```

The hover-tooltip lists `<date> — <reason>` for each entry, joined by `; `. Empty reasons render just the date.

### Legend

Add one swatch on the footer legend in `Index.cshtml`, between Gap and AvailableUnbooked:

```html
<span><span class="legend-swatch vt-dayoff border"></span>
  @Localizer["VolTrack_Legend_DayOff"]</span>
```

## Localization

15 new keys, added to all six locale resx files (English values shown; non-English files ship with `TODO: translate` comments as the codebase convention):

| Key | EN |
|---|---|
| `VolTrack_State_DayOff` | `Day off` |
| `VolTrack_Legend_DayOff` | `Day off (acknowledged)` |
| `VolTrack_Popover_MarkDayOff` | `Mark day off` |
| `VolTrack_Popover_CancelDayOff` | `Cancel day off` |
| `VolTrack_Popover_ReasonLabel` | `Reason (optional)` |
| `VolTrack_Popover_DayOffBlockedBySignups` | `Bail this signup before marking a day off` |
| `VolTrack_Popover_DayOffBlockedByCampSetup` | `Camp set-up is already covering this day` |
| `VolTrack_Msg_DayOffMarked` | `Day off marked.` |
| `VolTrack_Msg_DayOffCleared` | `Day off cleared.` |
| `VolTrack_DayOffBadge` | `{0} day off` |
| `VolTrack_Err_DayOffWithSignups` | `Volunteer has signups on that day — bail them first.` |
| `VolTrack_Err_DayOffDuringCampSetup` | `Camp set-up already covers that day.` |
| `VolTrack_Err_DayOffOutsideBuild` | `Day off must be inside the build period.` |
| `VolTrack_AuditAction_DayOffMarked` | `Day off marked` |
| `VolTrack_AuditAction_DayOffCleared` | `Day off cleared` |

(Same count as the keys removed in the previous teardown, so the resx files stay roughly the same size.)

## Migration

Single forward-only EF migration `AddVolunteerDayOffs`:

```csharp
protected override void Up(MigrationBuilder migrationBuilder)
{
    migrationBuilder.AddColumn<string>(
        name: "DayOffs",
        table: "volunteer_build_statuses",
        type: "jsonb",
        nullable: false,
        defaultValueSql: "'[]'::jsonb");
}

protected override void Down(MigrationBuilder migrationBuilder)
{
    migrationBuilder.DropColumn(name: "DayOffs", table: "volunteer_build_statuses");
}
```

Adds a column with a default — existing rows get `[]` automatically; no backfill code path. No destructive ops; the `NoDestructiveMigrationOps` ratchet baseline stays as-is.

## Testing

### Service unit tests (`VolunteerTrackingServiceTests`)

- `SetDayOffAsync_rejects_offset_outside_build_window` (above and below the window).
- `SetDayOffAsync_rejects_when_user_has_confirmed_signup_that_day`.
- `SetDayOffAsync_rejects_when_user_has_pending_signup_that_day`.
- `SetDayOffAsync_rejects_when_day_falls_inside_camp_setup`.
- `SetDayOffAsync_succeeds_when_day_is_a_gap` (asserts repo receives a fully-populated `DayOffEntry` with `MarkedByUserId` and `MarkedAt` from the clock).
- `SetDayOffAsync_replaces_reason_when_called_twice_on_same_day`.
- `SetDayOffAsync_trims_reason_and_truncates_at_200_chars`.
- `ClearDayOffAsync_removes_entry_when_present`.
- `ClearDayOffAsync_is_idempotent_when_entry_absent`.
- `MainCohort_dayoff_renders_DayOff_state_and_does_not_count_as_gap` (locks in cell-state precedence + `GapCount`).

`FakeVolunteerTrackingRepository` in the test file gains `UpsertDayOffAsync` and `RemoveDayOffAsync` (in-memory implementations, ~30 lines).

### Repository integration tests (`VolunteerTrackingRepositoryTests`)

- `UpsertDayOffAsync_inserts_first_entry_and_creates_row_if_absent`.
- `UpsertDayOffAsync_replaces_entry_for_same_day_offset`.
- `UpsertDayOffAsync_appends_entries_for_distinct_days_sorted_by_offset`.
- `RemoveDayOffAsync_drops_only_the_specified_day`.
- `RemoveDayOffAsync_returns_false_when_entry_absent`.
- `UpsertCampSetupAsync_does_not_disturb_existing_DayOffs` (parallel surfaces are independent).

### Controller tests (`VolunteerTrackingControllerTests`)

- Add `nameof(VolunteerTrackingController.SetDayOff)` and `nameof(VolunteerTrackingController.ClearDayOff)` to both `WriteActions_Require_VolunteerTrackingWrite_Policy` and `WriteActions_Have_AntiForgery_Validation` theories.
- `SetDayOff_HappyPath_RedirectsAndAuditsMarkedAction`.
- `SetDayOff_ServiceRejects_ReturnsBadRequestWithErrorTempData_NoAudit`.
- `ClearDayOff_HappyPath_RedirectsAndAuditsClearedAction`.
- `ClearDayOff_NoEntryToRemove_RedirectsWithoutAudit`.

### E2E tests (`tests/Humans.E2E.Tests/`)

Two new scenarios added to the existing volunteer-tracking spec:

- VC marks a day off via popover; cell renders striped-gray on next render; `GapCount` drops by one for that row.
- VC opens the popover on a cell with a confirmed signup; the popover shows the "Bail this signup before marking a day off" message instead of the button.

## Workflow Order

1. Domain entity gains `DayOffs` + `DayOffEntry` record.
2. EF configuration mapping (jsonb + `ValueComparer`).
3. Generate migration via `dotnet ef migrations add AddVolunteerDayOffs`.
4. Repository interface + implementation.
5. Service interface + implementation + result records.
6. Cell-state precedence (`VolunteerCellState.DayOff`, branches in both cohort builders that need it — main only).
7. `AuditAction` gains the two new values.
8. Controller actions + form models.
9. View partials — popover gating, legend, badge, CSS.
10. Resx — 15 new keys × 6 locales.
11. Tests — service + repo + controller + E2E.
12. Spec docs — `docs/features/47-volunteer-tracking.md` regains a "day off" subsection that points to this design doc.

Steps 1–7 leave the build green at every commit boundary so a reviewer can bisect cleanly. Step 8 is when the feature surfaces; 9–11 polish; 12 documents.

## Spec doc updates

After implementation, `docs/features/47-volunteer-tracking.md` is updated:

- The "removed pending redesign" note (added in the teardown commit) becomes a forward reference: "Day-off was redesigned in May 2026 — see [§ Day-off](#day-off) below."
- A new § Day-off section captures: actor (coord-only), the rules (signup-overlap, camp-setup-overlap, build-window), the cell rendering, and the audit verbs.
- Storage description: `VolunteerBuildStatus.DayOffs` jsonb column.
- The "Coordinator write actions" bullet list at the top adds: "mark / clear individual days off."

## Out of Scope

- Volunteer self-service surface — explicitly cut.
- Span/range entry UI — explicitly cut.
- "Block ahead" or "expected back date" — out.
- Unbooked-cohort interaction — out.
- Volunteer notification when marked off — out.
- `audit_log.RelatedEntityId` linkage to a specific entry within `DayOffs` — Approach 1 trade-off; out.

## Open Questions

None at design lock. Anything ambiguous in implementation will surface in code review.

## Related

- [`docs/features/47-volunteer-tracking.md`](../../features/47-volunteer-tracking.md) — feature doc for the parent volunteer-tracking page.
- [`docs/sections/Shifts.md`](../../sections/Shifts.md) — section invariant doc; the `VolunteerBuildStatus` sub-section is the canonical entity reference.
- [`memory/architecture/no-cross-section-ef-joins.md`](../../../memory/architecture/no-cross-section-ef-joins.md) — why `UserId` stays a bare `Guid`.
- [`memory/architecture/display-sort-in-controllers.md`](../../../memory/architecture/display-sort-in-controllers.md) — why the row sort lives in the controller.
