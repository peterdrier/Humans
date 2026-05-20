# Bugs — Recurrence expansion & Withdrawn moderation tab

Two independent Events-section bugs found 2026-05-20. Unrelated to the host/unified-submissions feature — each should land on its own branch off `main`.

---

## Bug 1 — Recurring events land on the wrong week

### Symptom
A recurring event submitted for e.g. Sunday with Tue + Wed recurrence days shows up on Tuesday and Wednesday of the **following** week in Browse (and everywhere else occurrences are expanded).

### Root cause — `src/Humans.Domain/Entities/Event.cs` (`GetOccurrenceInstants`, ~L170)
`RecurrenceDays` is stored as **absolute day-offsets from the gate-opening date**:
- The form checkboxes use `value="@day.DayOffset"` where `DayOffset` is the offset from gate-opening (`IndividualEventForm.cshtml`, `BarrioEventForm.cshtml`).
- The moderation view labels the stored value "Day offsets" (`EventsModeration/Index.cshtml:121`).

But `GetOccurrenceInstants` expands them as offsets **relative to `StartAt`**:

```csharp
.Select(dayOffset => StartAt.Plus(Duration.FromDays(dayOffset!.Value)))
```

So for `StartAt` = Sunday (gate-offset 5) with recurrence `"2,3"` (Tue, Wed), it produces `Sunday+2` and `Sunday+3` = Tue/Wed of the next week. The two interpretations of the same integer disagree.

### Affected consumers (all use `GetOccurrenceInstants`)
- `EventsController.Browse` (L397)
- `EventsExportController` (L100 PrintGuide, L149 CSV)
- `EventsDashboardController` (L57)
- `Api/EventsApiController.GetEvents` (L49)

### Fix
Interpret offsets as **absolute gate offsets**. Each occurrence = local date `(gateOpeningDate + offset)` at `StartAt`'s local time-of-day, converted back to an `Instant` in the event timezone:

- Change signature to `GetOccurrenceInstants(LocalDate gateOpeningDate, DateTimeZone tz)` (non-recurring still returns `[StartAt]`).
- Each occurrence: take `StartAt.InZone(tz).TimeOfDay`, combine with `gateOpeningDate.PlusDays(offset)`, resolve to `Instant` in `tz`.
- Update all 5 call sites — each already has `gateOpeningDate` and `tz` in scope.

**Acceptance:** an event whose recurrence days are Tue 7 Jul / Wed 8 Jul expands to exactly Tue 7 Jul and Wed 8 Jul, regardless of which day `StartDate` was set to.

### Tests
- Unit test on `GetOccurrenceInstants`: `StartAt` Sunday, recurrence `"2,3"` ⇒ occurrences on the gate-offset-2 and gate-offset-3 dates (same week), not +2/+3 from Sunday.
- Guard the non-recurring and empty-`RecurrenceDays` paths.

---

## Bug 2 — Withdrawn events not visible in the moderation queue

### Symptom
Withdrawn events never appear in `/Events/Moderate`. Suspected to be admin-only; it is not.

### Root cause — `src/Humans.Web/Controllers/EventsModerationController.cs`
The controller is already gated `[Authorize(Roles = RoleGroups.EventsAdminOrAdmin)]` (GuideModerator **or** Admin) — visibility is not role-restricted. The queue simply has **no Withdrawn tab**: `ModerationQueueViewModel` exposes counts for Pending / Approved / Rejected / ResubmitRequested only, and the view renders tabs for just those. `GetEventsByStatusAsync(EventStatus.Withdrawn)` and the status-counts query already support Withdrawn — it's just never requested.

### Fix
- Add `WithdrawnCount` to `ModerationQueueViewModel` (populate from `counts.GetValueOrDefault(EventStatus.Withdrawn)`).
- Add a Withdrawn tab to `EventsModeration/Index.cshtml` alongside the existing tabs.
- No new query needed; `Index(tab: Withdrawn)` already works via `GetEventsByStatusAsync`.
- Withdrawn is terminal — the row's action buttons should be suppressed for that tab (view-only).

**Acceptance:** a GuideModerator (not just Admin) sees a Withdrawn tab with the correct count and the list of withdrawn events.

### Tests
- Moderation queue integration test: withdrawn event appears under the Withdrawn tab; count is correct; visible to a GuideModerator.
