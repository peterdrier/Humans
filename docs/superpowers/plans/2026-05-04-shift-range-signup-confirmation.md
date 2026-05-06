# Shift range signup — confirmation modal — implementation plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the direct-submit "Sign up for setup/strike" button on the Build/Strike rota partial with a Bootstrap confirmation modal that shows the chosen range, day count, on-site arrival expectation, and any conflicts with the user's existing signups (cross-event, time-window-aware), so accidental misclicks don't create unintended multi-day commitments.

**Architecture:**
- **Service layer:** `ShiftSignupService.SignUpRangeAsync` grows a `bool skipConflicts = false` parameter. When `true`, days already signed up or time-overlapping with the user's other active signups are filtered out instead of failing the whole range; the skip summary is returned via `SignupResult.Warning` so the controller's existing toast surfacing displays it.
- **View layer:** The `_BuildStrikeRotaTable.cshtml` partial gains a per-rota Bootstrap modal that lives inside the form, two `<script type="application/json">` data blobs (the user's active signups across all events; the rota's per-day-offset absolute time windows), and a small inline `show.bs.modal` handler that computes overlapping days client-side from those blobs (display only — server enforces).
- **Controller layer:** `ShiftsController.Index` loads the user's active signups via the existing `IShiftSignupRepository.GetActiveSignupsForUserAsync` and projects them into a new `UserSignupConflictItem[]` on `BuildStrikeRotaTableViewModel`. `ShiftsController.SignUpRange` passes `skipConflicts: true` so the user-initiated dashboard flow matches the modal's promise.
- **Localization:** New `Shifts_ConfirmSignup_*` keys are added to all six locale resx files (`SharedResource.{resx,de.resx,es.resx,ca.resx,fr.resx,it.resx}`).

**Tech Stack:**
- ASP.NET Core 9 MVC + Razor partial views (`.cshtml`).
- Bootstrap 5 modal (`data-bs-toggle="modal"`).
- NodaTime (`Instant`, `LocalTime`, `LocalDate`) for time math — already pervasive.
- xUnit for service tests in `tests/Humans.Application.Tests/Services/ShiftSignupServiceTests.cs`.
- Six locale resx files in `src/Humans.Web/Resources/`.

**Spec:** [`docs/superpowers/specs/2026-05-04-shift-range-signup-confirmation-design.md`](../specs/2026-05-04-shift-range-signup-confirmation-design.md)

---

## File structure

| File | Role | Status |
|---|---|---|
| `src/Humans.Application/Services/Shifts/ShiftSignupService.cs` | `SignUpRangeAsync` reworked to accept `bool skipConflicts`, filter (not reject) when true, return warning summary. | Modify (~80 lines net new in one method) |
| `src/Humans.Application/Interfaces/Shifts/IShiftSignupService.cs` | Interface signature updated. | Modify (1 line) |
| `tests/Humans.Application.Tests/Services/ShiftSignupServiceTests.cs` | New unit tests for `skipConflicts: true`. | Modify (~150 lines added in `// SignUpRange` region) |
| `src/Humans.Web/Models/ShiftViewModels.cs` | `BuildStrikeRotaTableViewModel` gains `UserActiveSignups`, `RotaWindowsByDayOffset`. New record types `UserSignupConflictItem`, `ShiftWindow`. | Modify (~40 lines added near line 548) |
| `src/Humans.Web/Controllers/ShiftsController.cs` | `Index` loads cross-event active signups + computes per-day-offset windows; passes them through to the view-model construction site (which is in the view itself). `SignUpRange` passes `skipConflicts: true`. | Modify (~25 lines added in `Index`, 1 line changed in `SignUpRange`) |
| `src/Humans.Web/Models/ShiftViewModels.cs` (`ShiftBrowseViewModel`) | Gains `UserActiveSignups` so the partial-construction site at `Views/Shifts/Index.cshtml:364` can pass it through. | Modify (~5 lines) |
| `src/Humans.Web/Views/Shifts/Index.cshtml` | Pass new fields through to `_BuildStrikeRotaTable` partial constructor (lines 364-377 and 513). | Modify (~6 lines, two call sites) |
| `src/Humans.Web/Views/Shared/_BuildStrikeRotaTable.cshtml` | Button → `type="button"` + `data-bs-toggle`; new modal markup; `data-date` on options; two JSON blobs; inline `<script>` for show.bs.modal handler. | Modify (~80 lines added) |
| `src/Humans.Web/Resources/SharedResource.resx` | New `Shifts_ConfirmSignup_*` keys (English). | Modify |
| `src/Humans.Web/Resources/SharedResource.de.resx` | German translations. | Modify |
| `src/Humans.Web/Resources/SharedResource.es.resx` | Spanish translations (highest-quality non-English, hand-translated). | Modify |
| `src/Humans.Web/Resources/SharedResource.ca.resx` | Catalan translations. | Modify |
| `src/Humans.Web/Resources/SharedResource.fr.resx` | French translations. | Modify |
| `src/Humans.Web/Resources/SharedResource.it.resx` | Italian translations. | Modify |

No new files. No DB migrations. No new routes. No new repository or service methods (only a new parameter on an existing method).

---

## Chunk 1: Service change — `SignUpRangeAsync(skipConflicts)`

**Goal:** Add `bool skipConflicts = false` to `ShiftSignupService.SignUpRangeAsync`. When true, days where the user is already signed up or where there's a time-window conflict are filtered out instead of failing the whole range. Skip summary returned via `SignupResult.Warning`. TDD throughout.

**Why first:** The view layer's "Sign-up will only add days that don't conflict" promise depends on the server keeping it. Locking down the server contract before touching the view means the modal never makes a promise the server can't keep.

### Task 1.1: Read context

**Files (read only):**
- `src/Humans.Application/Services/Shifts/ShiftSignupService.cs:526-700` — current `SignUpRangeAsync` body
- `src/Humans.Application/Interfaces/Shifts/IShiftSignupService.cs:59` — current signature
- `src/Humans.Domain/Entities/Shift.cs:64-132` — `AllDayWindow*` constants and `GetAbsoluteStart/End` methods
- `tests/Humans.Application.Tests/Services/ShiftSignupServiceTests.cs:371-535` (existing `SignUpRange` tests) and `:630-740` (helper methods)

- [ ] **Step 1: Read the four files above and note these conventions** (every test snippet in this chunk uses these — do not invent new helpers or fixture names):

  - **Test attribute:** `[HumansFact]` (NOT `[Fact]`). Used on every test in this file.
  - **DbContext field:** `_dbContext` (NOT `_db`).
  - **Clock:** `TestNow` static `Instant` field (= `2026-06-15 12:00 UTC`). Use `TestNow` for `CreatedAt` / `UpdatedAt`, NOT `_clock.GetCurrentInstant()`.
  - **Assertions:** AwesomeAssertions `.Should().Be(...)`, `.Should().Contain(...)`, `.Should().HaveCount(...)`. NOT `Assert.*`.
  - **Existing helpers in the same test class** (lines 634-740):
    - `(EventSettings es, Rota rota, Shift shift) SeedShiftScenario(SignupPolicy policy, ShiftPriority priority = ShiftPriority.Normal)` — creates an EventSettings (Madrid TZ, gate opens 2026-07-01, browsing open), a Team, a Rota (period defaults to `Event` — the new tests reassign to `Build`), and one Shift (day 1, 10:00, 4h). Sets `rota.EventSettings = es` for in-memory provider.
    - `Shift SeedShift(Rota rota, int dayOffset, int startHour, double durationHours)` — adds a timed shift; sets `shift.Rota = rota`.
    - `Shift SeedAllDayShift(Rota rota, int dayOffset)` — adds an all-day shift; sets `shift.Rota = rota`.
    - `ShiftSignup SeedSignup(Guid userId, Guid shiftId, SignupStatus status)` — adds a signup with `TestNow` timestamps.
  - **Service-side context:**
    - The duplicate-signup check at lines 556-560 (currently rejects whole range).
    - The overlap check at lines 562-590 (currently rejects whole range with `Time conflict on day(s)`).
    - The capacity-check `warning` plumbing at lines 593-612 (existing pattern for "soft" skips with a warning — we're matching it).

  No commit needed — this is orientation only.

### Task 1.2: Add interface signature for `skipConflicts`

**Files:**
- Modify: `src/Humans.Application/Interfaces/Shifts/IShiftSignupService.cs:59`

- [ ] **Step 1: Update interface signature**

  Change line 59 from:
  ```csharp
  Task<SignupResult> SignUpRangeAsync(Guid userId, Guid rotaId, int startDayOffset, int endDayOffset, Guid? actorUserId = null, bool isPrivileged = false);
  ```
  to:
  ```csharp
  Task<SignupResult> SignUpRangeAsync(Guid userId, Guid rotaId, int startDayOffset, int endDayOffset, Guid? actorUserId = null, bool isPrivileged = false, bool skipConflicts = false);
  ```

- [ ] **Step 2: Update implementation signature in `ShiftSignupService.cs:526` to match**

  ```csharp
  public async Task<SignupResult> SignUpRangeAsync(Guid userId, Guid rotaId, int startDayOffset, int endDayOffset, Guid? actorUserId = null, bool isPrivileged = false, bool skipConflicts = false)
  ```

  No body change yet — just signatures.

- [ ] **Step 3: Build to confirm signatures compile**

  Run: `dotnet build Humans.slnx -v quiet`
  Expected: build succeeds, no warnings.

- [ ] **Step 4: Run existing `SignUpRange*` tests to confirm nothing broke**

  Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~SignUpRange"`
  Expected: all green (skipConflicts defaults to false, no behaviour change).

- [ ] **Step 5: Commit signature-only change**

  ```bash
  git add src/Humans.Application/Interfaces/Shifts/IShiftSignupService.cs src/Humans.Application/Services/Shifts/ShiftSignupService.cs
  git commit -m "$(cat <<'EOF'
  feat(shifts): add skipConflicts parameter to SignUpRangeAsync (signature only)

  Defaults to false so existing callers preserve strict behaviour.
  Body changes follow.

  Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
  EOF
  )"
  ```

### Task 1.3: Test — `skipConflicts: true` skips already-signed-up days

**Files:**
- Modify: `tests/Humans.Application.Tests/Services/ShiftSignupServiceTests.cs` (append to the `// SignUpRange` region after the existing `SignUpRange_BlocksIfAnyDayOverlaps` test, ~line 421)

- [ ] **Step 1: Write failing test for partial-overlap-with-existing-signup-in-range case**

  Use the existing `SeedShiftScenario` / `SeedAllDayShift` / `SeedSignup` helpers — copy the imperative pattern from `SignUpRange_BlocksIfAnyDayOverlaps` (lines 399-421).

  ```csharp
  [HumansFact]
  public async Task SignUpRange_SkipConflicts_FiltersAlreadySignedUpDays()
  {
      // Arrange: rota with 3 all-day shifts; user already signed up to day -2 in same rota.
      var (es, rota, _) = SeedShiftScenario(SignupPolicy.Public);
      rota.Period = RotaPeriod.Build;
      for (var day = -3; day <= -1; day++)
          SeedAllDayShift(rota, day);
      var userId = Guid.NewGuid();
      await _dbContext.SaveChangesAsync();

      var dayMinus2Shift = await _dbContext.Shifts
          .FirstAsync(s => s.RotaId == rota.Id && s.DayOffset == -2);
      var existingSignup = SeedSignup(userId, dayMinus2Shift.Id, SignupStatus.Confirmed);
      await _dbContext.SaveChangesAsync();

      // Act
      var result = await _service.SignUpRangeAsync(userId, rota.Id, -3, -1, skipConflicts: true);

      // Assert
      result.Success.Should().BeTrue();
      result.Warning.Should().NotBeNull();
      result.Warning.Should().Contain("Already signed up");

      var signups = await _dbContext.ShiftSignups
          .Where(s => s.UserId == userId)
          .ToListAsync();
      signups.Should().HaveCount(3); // 1 pre-existing + 2 new
      var newOffsets = signups
          .Where(s => s.Id != existingSignup.Id)
          .Join(_dbContext.Shifts, s => s.ShiftId, sh => sh.Id, (s, sh) => sh.DayOffset)
          .OrderBy(o => o)
          .ToList();
      newOffsets.Should().Equal(-3, -1);
  }
  ```

- [ ] **Step 2: Run test to verify it fails**

  Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~SignUpRange_SkipConflicts_FiltersAlreadySignedUpDays"`
  Expected: FAIL — current body still rejects the whole range with `"Already signed up for one or more shifts in this range."`. The new assertion `result.Success.Should().BeTrue()` is the one that fails.

### Task 1.4: Implement skip-already-signed-up branch

**Files:**
- Modify: `src/Humans.Application/Services/Shifts/ShiftSignupService.cs:556-560`

This task introduces both `alreadySignedUpDays` and `skipMessages` in their **final** positions — Task 1.6 then only adds to them. No move/refactor between tasks.

- [ ] **Step 1: Replace the duplicate-signup hard-fail with a conditional filter, and prepare the skip-summary list at the same time**

  Change lines 556-560 from:
  ```csharp
  // Duplicate signup check — reject if user already has Pending/Confirmed on any shift in range (Fix #1)
  var shiftIdsInRange = shiftsInRange.Select(s => s.Id).ToHashSet();
  var activeShiftIds = await _repo.GetActiveShiftIdsForUserAsync(userId, shiftIdsInRange);
  if (activeShiftIds.Count > 0)
      return SignupResult.Fail("Already signed up for one or more shifts in this range.");
  ```
  to:
  ```csharp
  // Duplicate signup check — reject (or filter, if skipConflicts) if user already has Pending/Confirmed
  var shiftIdsInRange = shiftsInRange.Select(s => s.Id).ToHashSet();
  var activeShiftIds = await _repo.GetActiveShiftIdsForUserAsync(userId, shiftIdsInRange);
  var skipMessages = new List<string>();
  if (activeShiftIds.Count > 0)
  {
      if (!skipConflicts)
          return SignupResult.Fail("Already signed up for one or more shifts in this range.");

      var alreadySignedUpDays = shiftsInRange
          .Where(s => activeShiftIds.Contains(s.Id))
          .Select(s => s.DayOffset)
          .ToList();
      var dayList = string.Join(", ", alreadySignedUpDays.Select(offset =>
          FormatShiftDate(es.GateOpeningDate.PlusDays(offset))));
      skipMessages.Add($"Already signed up for day(s): {dayList}.");

      shiftsInRange = shiftsInRange.Where(s => !activeShiftIds.Contains(s.Id)).ToList();
  }
  ```

  Reassigning a `var`-typed local (`shiftsInRange`) to the same `List<Shift>` type is legal C#; no declaration change to line 547 needed.

- [ ] **Step 2: Change the existing `string? warning = null;` declaration (currently line 593) to consume `skipMessages`**

  ```csharp
  string? warning = skipMessages.Count > 0 ? string.Join(" ", skipMessages) : null;
  ```

  Existing capacity-check / EE-cap-check code below it already uses the `warning = warning is null ? msg : $"{warning} {msg}";` pattern — no change there.

- [ ] **Step 3: Re-run the new test**

  Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~SignUpRange_SkipConflicts_FiltersAlreadySignedUpDays"`
  Expected: PASS.

- [ ] **Step 4: Run all existing SignUpRange tests to confirm strict path unchanged**

  Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~SignUpRange"`
  Expected: all green (the original `SignUpRange_BlocksIfAnyDayOverlaps` still passes because `skipConflicts` defaults false).

- [ ] **Step 5: Commit**

  ```bash
  git add src/Humans.Application/Services/Shifts/ShiftSignupService.cs tests/Humans.Application.Tests/Services/ShiftSignupServiceTests.cs
  git commit -m "$(cat <<'EOF'
  feat(shifts): SignUpRangeAsync skipConflicts filters duplicate-signup days

  When skipConflicts: true and the user is already signed up to one or
  more shifts in the range, those days are dropped and the rest of the
  range proceeds; skip summary surfaces in SignupResult.Warning.

  Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
  EOF
  )"
  ```

### Task 1.5: Test — `skipConflicts: true` skips time-overlapping days

**Files:**
- Modify: `tests/Humans.Application.Tests/Services/ShiftSignupServiceTests.cs`

- [ ] **Step 1: Write failing test for cross-rota time overlap**

  Note: the existing `SeedAllDayShift` helper produces all-day shifts whose absolute window is `08:00–18:00` event-local (per `Shift.AllDayWindowStart/End`). A 12:00–14:00 timed shift on the same day clearly overlaps.

  Critical detail (per chunk-1 review): manually-constructed `Rota` and `Shift` entities **must** have their nav properties set (`shift.Rota = ...`, `rota.EventSettings = ...`) for the in-memory provider to return them on subsequent reads — see how `SeedShift` does it at line 701, and `SeedShiftScenario` at line 681.

  ```csharp
  [HumansFact]
  public async Task SignUpRange_SkipConflicts_FiltersTimeOverlappingDays()
  {
      // Arrange: Build rota + a separate Event rota with a 12:00-14:00 shift on day -2.
      var (es, buildRota, _) = SeedShiftScenario(SignupPolicy.Public);
      buildRota.Period = RotaPeriod.Build;
      for (var day = -3; day <= -1; day++)
          SeedAllDayShift(buildRota, day);

      var otherRota = new Rota
      {
          Id = Guid.NewGuid(),
          Name = "Kitchen",
          EventSettingsId = es.Id,
          Period = RotaPeriod.Event,
          Policy = SignupPolicy.Public,
          TeamId = buildRota.TeamId,
          CreatedAt = TestNow,
          UpdatedAt = TestNow
      };
      otherRota.EventSettings = es; // nav property for in-memory provider
      _dbContext.Rotas.Add(otherRota);

      var conflictingShift = new Shift
      {
          Id = Guid.NewGuid(),
          RotaId = otherRota.Id,
          DayOffset = -2,
          StartTime = new LocalTime(12, 0),
          Duration = Duration.FromHours(2),
          MinVolunteers = 1,
          MaxVolunteers = 5,
          IsAllDay = false,
          CreatedAt = TestNow,
          UpdatedAt = TestNow
      };
      conflictingShift.Rota = otherRota; // nav property for in-memory provider
      _dbContext.Shifts.Add(conflictingShift);

      var userId = Guid.NewGuid();
      SeedSignup(userId, conflictingShift.Id, SignupStatus.Confirmed);
      await _dbContext.SaveChangesAsync();

      // Act
      var result = await _service.SignUpRangeAsync(userId, buildRota.Id, -3, -1, skipConflicts: true);

      // Assert
      result.Success.Should().BeTrue();
      result.Warning.Should().NotBeNull();
      result.Warning.Should().Contain("Time conflict");

      var newOffsets = await _dbContext.ShiftSignups
          .Where(s => s.UserId == userId && s.Shift!.RotaId == buildRota.Id)
          .Join(_dbContext.Shifts, s => s.ShiftId, sh => sh.Id, (s, sh) => sh.DayOffset)
          .OrderBy(o => o)
          .ToListAsync();
      newOffsets.Should().Equal(-3, -1);
  }
  ```

- [ ] **Step 2: Run test to verify it fails**

  Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~SignUpRange_SkipConflicts_FiltersTimeOverlappingDays"`
  Expected: FAIL — service still hard-fails at the time-conflict check (lines 585-590) with `"Time conflict on day(s): ..."`. The new assertion `result.Success.Should().BeTrue()` is the one that fails.

### Task 1.6: Implement skip-time-conflict branch + empty-range guard

**Files:**
- Modify: `src/Humans.Application/Services/Shifts/ShiftSignupService.cs:585-590`

`skipMessages` is already declared in its final position by Task 1.4 (right after the duplicate-signup filter). This task only adds to it.

- [ ] **Step 1: Replace the time-conflict hard-fail with a conditional filter**

  Change the existing `if (conflictingDays.Count > 0) { ... return SignupResult.Fail(...); }` block (currently lines 585-590) to:

  ```csharp
  if (conflictingDays.Count > 0)
  {
      var dayList = string.Join(", ", conflictingDays.Select(offset =>
          FormatShiftDate(es.GateOpeningDate.PlusDays(offset))));

      if (!skipConflicts)
          return SignupResult.Fail($"Time conflict on day(s): {dayList}.");

      skipMessages.Add($"Time conflict on day(s): {dayList}.");
      shiftsInRange = shiftsInRange.Where(s => !conflictingDays.Contains(s.DayOffset)).ToList();
  }

  // Empty-range guard — covers both "filtered everything out" and "range was always empty"
  if (shiftsInRange.Count == 0)
  {
      return skipMessages.Count > 0
          ? SignupResult.Fail(string.Join(" ", skipMessages) + " Nothing to add.")
          : SignupResult.Fail("No shifts found in the specified date range.");
  }
  ```

  The new empty-range guard supersedes the original `if (shiftsInRange.Count == 0) return SignupResult.Fail("No shifts found...");` check at line 549-550 — **delete that earlier check**, since the same condition is now caught after filtering.

- [ ] **Step 2: Re-run the new test**

  Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~SignUpRange_SkipConflicts_FiltersTimeOverlappingDays"`
  Expected: PASS.

- [ ] **Step 3: Run all SignUpRange* tests**

  Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~SignUpRange"`
  Expected: all green.

- [ ] **Step 4: Commit**

  ```bash
  git add src/Humans.Application/Services/Shifts/ShiftSignupService.cs tests/Humans.Application.Tests/Services/ShiftSignupServiceTests.cs
  git commit -m "$(cat <<'EOF'
  feat(shifts): SignUpRangeAsync skipConflicts filters time-overlapping days

  Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
  EOF
  )"
  ```

### Task 1.7: Test — every day conflicts → fail with summary, no signups created

**Files:**
- Modify: `tests/Humans.Application.Tests/Services/ShiftSignupServiceTests.cs`

- [ ] **Step 1: Write the test**

  ```csharp
  [HumansFact]
  public async Task SignUpRange_SkipConflicts_AllDaysConflict_ReturnsFailWithSummary()
  {
      // Arrange: rota with 3 all-day shifts, user already signed up to ALL three.
      var (es, rota, _) = SeedShiftScenario(SignupPolicy.Public);
      rota.Period = RotaPeriod.Build;
      var userId = Guid.NewGuid();
      var shifts = new List<Shift>();
      for (var day = -3; day <= -1; day++)
          shifts.Add(SeedAllDayShift(rota, day));
      await _dbContext.SaveChangesAsync();

      foreach (var shift in shifts)
          SeedSignup(userId, shift.Id, SignupStatus.Confirmed);
      await _dbContext.SaveChangesAsync();

      // Act
      var result = await _service.SignUpRangeAsync(userId, rota.Id, -3, -1, skipConflicts: true);

      // Assert
      result.Success.Should().BeFalse();
      result.Error.Should().NotBeNull();
      result.Error.Should().Contain("Nothing to add");

      var totalSignups = await _dbContext.ShiftSignups.CountAsync(s => s.UserId == userId);
      totalSignups.Should().Be(3); // only the 3 pre-existing
  }
  ```

- [ ] **Step 2: Run — expect PASS already**

  Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~SignUpRange_SkipConflicts_AllDaysConflict"`
  Expected: PASS — the empty-range guard added in Task 1.6 already covers this case.

  If it fails, the most likely cause is the `Nothing to add` substring not making it into the error — verify the error-construction code path covers the already-signed-up-only branch (no time-conflict step needed).

- [ ] **Step 3: Commit**

  ```bash
  git add tests/Humans.Application.Tests/Services/ShiftSignupServiceTests.cs
  git commit -m "$(cat <<'EOF'
  test(shifts): cover SignUpRangeAsync skipConflicts full-block case

  Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
  EOF
  )"
  ```

### Task 1.7b: Test — mixed already-signed-up + time-conflict + free → free days get signed up

**Why this exists:** spec line 203 explicitly calls for this case. Easy to miss with two separate skip kinds; needs its own assertion that **both** skip kinds appear distinctly in the warning string.

**Files:**
- Modify: `tests/Humans.Application.Tests/Services/ShiftSignupServiceTests.cs`

- [ ] **Step 1: Write the test**

  ```csharp
  [HumansFact]
  public async Task SignUpRange_SkipConflicts_MixedKinds_AddsFreeDaysWithBothWarnings()
  {
      // Arrange: range -4..-1.
      //   day -4: free
      //   day -3: already signed up to this same Build rota
      //   day -2: cross-rota 12:00-14:00 conflict
      //   day -1: free
      var (es, buildRota, _) = SeedShiftScenario(SignupPolicy.Public);
      buildRota.Period = RotaPeriod.Build;
      for (var day = -4; day <= -1; day++)
          SeedAllDayShift(buildRota, day);

      var otherRota = new Rota
      {
          Id = Guid.NewGuid(),
          Name = "Kitchen",
          EventSettingsId = es.Id,
          Period = RotaPeriod.Event,
          Policy = SignupPolicy.Public,
          TeamId = buildRota.TeamId,
          CreatedAt = TestNow,
          UpdatedAt = TestNow
      };
      otherRota.EventSettings = es;
      _dbContext.Rotas.Add(otherRota);

      var crossRotaShift = new Shift
      {
          Id = Guid.NewGuid(),
          RotaId = otherRota.Id,
          DayOffset = -2,
          StartTime = new LocalTime(12, 0),
          Duration = Duration.FromHours(2),
          MinVolunteers = 1,
          MaxVolunteers = 5,
          IsAllDay = false,
          CreatedAt = TestNow,
          UpdatedAt = TestNow
      };
      crossRotaShift.Rota = otherRota;
      _dbContext.Shifts.Add(crossRotaShift);

      var userId = Guid.NewGuid();
      await _dbContext.SaveChangesAsync();

      var dayMinus3Shift = await _dbContext.Shifts
          .FirstAsync(s => s.RotaId == buildRota.Id && s.DayOffset == -3);
      SeedSignup(userId, dayMinus3Shift.Id, SignupStatus.Confirmed);  // already-signed-up case
      SeedSignup(userId, crossRotaShift.Id, SignupStatus.Confirmed);  // time-conflict case
      await _dbContext.SaveChangesAsync();

      // Act
      var result = await _service.SignUpRangeAsync(userId, buildRota.Id, -4, -1, skipConflicts: true);

      // Assert: -4 and -1 added; both warning kinds present
      result.Success.Should().BeTrue();
      result.Warning.Should().NotBeNull();
      result.Warning.Should().Contain("Already signed up");
      result.Warning.Should().Contain("Time conflict");

      var newOffsets = await _dbContext.ShiftSignups
          .Where(s => s.UserId == userId && s.Shift!.RotaId == buildRota.Id && s.ShiftId != dayMinus3Shift.Id)
          .Join(_dbContext.Shifts, s => s.ShiftId, sh => sh.Id, (s, sh) => sh.DayOffset)
          .OrderBy(o => o)
          .ToListAsync();
      newOffsets.Should().Equal(-4, -1);
  }
  ```

- [ ] **Step 2: Run**

  Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~SignUpRange_SkipConflicts_MixedKinds"`
  Expected: PASS — the implementation in 1.4 + 1.6 already supports this; this test pins it.

- [ ] **Step 3: Commit**

  ```bash
  git add tests/Humans.Application.Tests/Services/ShiftSignupServiceTests.cs
  git commit -m "$(cat <<'EOF'
  test(shifts): pin SignUpRangeAsync skipConflicts mixed-kinds behaviour

  Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
  EOF
  )"
  ```

### Task 1.8: Test — strict mode (skipConflicts: false) preserves error path

**Files:**
- Modify: `tests/Humans.Application.Tests/Services/ShiftSignupServiceTests.cs`

The existing `SignUpRange_BlocksIfAnyDayOverlaps` test (line 399-421) already pins strict-mode behaviour for the duplicate-signup path. This task adds the equivalent pin for the **time-conflict** path (which the existing tests don't cover).

- [ ] **Step 1: Write the test (no production change expected)**

  ```csharp
  [HumansFact]
  public async Task SignUpRange_StrictMode_TimeOverlap_PreservesHardFail()
  {
      // Arrange: cross-rota time conflict on day -2 (same shape as Task 1.5).
      var (es, buildRota, _) = SeedShiftScenario(SignupPolicy.Public);
      buildRota.Period = RotaPeriod.Build;
      for (var day = -3; day <= -1; day++)
          SeedAllDayShift(buildRota, day);

      var otherRota = new Rota
      {
          Id = Guid.NewGuid(),
          Name = "Kitchen",
          EventSettingsId = es.Id,
          Period = RotaPeriod.Event,
          Policy = SignupPolicy.Public,
          TeamId = buildRota.TeamId,
          CreatedAt = TestNow,
          UpdatedAt = TestNow
      };
      otherRota.EventSettings = es;
      _dbContext.Rotas.Add(otherRota);

      var conflictingShift = new Shift
      {
          Id = Guid.NewGuid(),
          RotaId = otherRota.Id,
          DayOffset = -2,
          StartTime = new LocalTime(12, 0),
          Duration = Duration.FromHours(2),
          MinVolunteers = 1,
          MaxVolunteers = 5,
          IsAllDay = false,
          CreatedAt = TestNow,
          UpdatedAt = TestNow
      };
      conflictingShift.Rota = otherRota;
      _dbContext.Shifts.Add(conflictingShift);

      var userId = Guid.NewGuid();
      SeedSignup(userId, conflictingShift.Id, SignupStatus.Confirmed);
      await _dbContext.SaveChangesAsync();

      // Act — no skipConflicts argument; defaults to false.
      var result = await _service.SignUpRangeAsync(userId, buildRota.Id, -3, -1);

      // Assert: hard-fails with the legacy error message; nothing new written.
      result.Success.Should().BeFalse();
      result.Error.Should().NotBeNull();
      result.Error.Should().Contain("Time conflict");

      var newSignupCount = await _dbContext.ShiftSignups
          .Where(s => s.UserId == userId && s.Shift!.RotaId == buildRota.Id)
          .CountAsync();
      newSignupCount.Should().Be(0);
  }
  ```

  Refactoring tip: tasks 1.5, 1.7b, and 1.8 share the cross-rota-conflict-shift setup. After committing, if the duplication smells real, extract a private helper `(Rota Other, Shift ConflictingShift) SeedCrossRotaConflict(EventSettings es, Rota buildRota, int dayOffset)`. Don't over-extract — only if it cleans up.

- [ ] **Step 2: Run — expect PASS**

  Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~SignUpRange_StrictMode_TimeOverlap"`
  Expected: PASS — `skipConflicts` defaults to false, behaviour unchanged.

- [ ] **Step 3: Commit**

  ```bash
  git add tests/Humans.Application.Tests/Services/ShiftSignupServiceTests.cs
  git commit -m "$(cat <<'EOF'
  test(shifts): pin SignUpRangeAsync strict-mode time-conflict hard-fail

  Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
  EOF
  )"
  ```

### Task 1.9: Run full test suite

- [ ] **Step 1: Build + run all tests**

  Run: `dotnet test Humans.slnx -v quiet`
  Expected: 0 failures.

  If anything else in the codebase (e.g., other range-using callers) was broken by the new optional parameter, fix the call sites — but the parameter has a default so this should not happen.

---

## Chunk 2: Controller + view-model — load and pass conflict data

**Goal:** Load the user's active signups (cross-event) in `ShiftsController.Index`, project to a small DTO, plumb through to `BuildStrikeRotaTableViewModel.UserActiveSignups` and `RotaWindowsByDayOffset`. Update `SignUpRange` to call the service with `skipConflicts: true`.

### Task 2.1: Add record types and view-model fields

**Files:**
- Modify: `src/Humans.Web/Models/ShiftViewModels.cs` (insert before line 548 where `BuildStrikeRotaTableViewModel` lives)

**Note on `set;` vs `init;`:** the spec showed `{ get; init; }` for the new properties. The plan uses `{ get; set; }` to match the existing pattern across `BuildStrikeRotaTableViewModel` (verified: lines 548-561 are all `set;`). This is an intentional consistency choice over the spec's prose example.

**Note on `ShiftName`:** the spec hinted at "Kitchen Lunch"-style shift names in conflict-row copy. The `Shift` entity has no `Name` property — only an optional markdown `Description`. Including markdown in a tooltip-style row is messy; the `/Shifts/Mine` page just displays `Rota.Name` (e.g., "Kitchen") + the time range, which is unambiguous within a rota. **The plan drops `ShiftName` and uses just `RotaName` + display times.** The localizer key `Shifts_ConfirmSignup_Conflicts_Row` is updated accordingly in Chunk 4.

- [ ] **Step 1: Define record types and add fields**

  Insert immediately above `public class BuildStrikeRotaTableViewModel` (i.e., before line 548):

  ```csharp
  public record UserSignupConflictItem(
      LocalDate Date,
      string RotaName,
      Instant AbsoluteStart,
      Instant AbsoluteEnd,
      string DisplayStart,
      string DisplayEnd);

  public record ShiftWindow(Instant AbsoluteStart, Instant AbsoluteEnd);
  ```

  Inside `BuildStrikeRotaTableViewModel`, add:

  ```csharp
  public IReadOnlyList<UserSignupConflictItem> UserActiveSignups { get; set; } = [];
  public IReadOnlyDictionary<int, ShiftWindow> RotaWindowsByDayOffset { get; set; } = new Dictionary<int, ShiftWindow>();
  ```

  Add `using NodaTime;` at the top of the file if it isn't already imported (the existing code uses `LocalDate`, so it likely is — check first).

- [ ] **Step 2: Add `UserActiveSignups` to `ShiftBrowseViewModel` (parent VM)**

  Find `ShiftBrowseViewModel` (around line 140 of the same file) and add:

  ```csharp
  public IReadOnlyList<UserSignupConflictItem> UserActiveSignups { get; set; } = [];
  ```

  This is what the dashboard view (`Views/Shifts/Index.cshtml`) reads when constructing the partial's VM. Per-rota window dictionaries don't go on the parent VM — those are computed per-rota inline in the dashboard view, since they're cheap and rota-specific.

- [ ] **Step 3: Build to confirm compiles**

  Run: `dotnet build Humans.slnx -v quiet`
  Expected: build succeeds.

- [ ] **Step 4: Commit**

  ```bash
  git add src/Humans.Web/Models/ShiftViewModels.cs
  git commit -m "$(cat <<'EOF'
  feat(shifts): add UserActiveSignups + RotaWindowsByDayOffset to view-models

  Empty defaults so the dashboard renders unchanged until the controller
  is updated in the next commit.

  Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
  EOF
  )"
  ```

### Task 2.2: Load active signups in `ShiftsController.Index`

**Files:**
- Modify: `src/Humans.Application/Interfaces/Shifts/IShiftSignupService.cs` (add passthrough signature)
- Modify: `src/Humans.Application/Services/Shifts/ShiftSignupService.cs` (add passthrough body)
- Modify: `src/Humans.Web/Controllers/ShiftsController.cs` (around lines 65 and 238-260)

**Verified:** `IShiftSignupService` does NOT currently expose `GetActiveSignupsForUserAsync` — only the repository does. We add a passthrough on the service (same shape as `GetByUserAsync` at `ShiftSignupService.cs:867-868`) so the controller doesn't need a new constructor dependency.

- [ ] **Step 1: Add passthrough to `IShiftSignupService`**

  In `src/Humans.Application/Interfaces/Shifts/IShiftSignupService.cs`, after the existing `GetByUserAsync` declaration, add:

  ```csharp
  Task<IReadOnlyList<ShiftSignup>> GetActiveSignupsForUserAsync(Guid userId, CancellationToken ct = default);
  ```

  Place it next to `GetByUserAsync` to match the existing organisation.

- [ ] **Step 2: Add passthrough to `ShiftSignupService`**

  In `src/Humans.Application/Services/Shifts/ShiftSignupService.cs`, near line 867-868 (the existing `GetByUserAsync` passthrough), add:

  ```csharp
  public Task<IReadOnlyList<ShiftSignup>> GetActiveSignupsForUserAsync(Guid userId, CancellationToken ct = default) =>
      _repo.GetActiveSignupsForUserAsync(userId, ct);
  ```

- [ ] **Step 3: In `ShiftsController.Index`, after the existing `var userSignups = ...` line (line 65), add cross-event load**

  ```csharp
  var allActiveSignups = await _signupService.GetActiveSignupsForUserAsync(user.Id);

  var userActiveSignupsForUi = allActiveSignups
      .Where(s => s.Shift?.Rota?.EventSettings is not null)
      .Select(s =>
      {
          var sEs = s.Shift!.Rota!.EventSettings!;
          var absStart = s.Shift.GetAbsoluteStart(sEs);
          var absEnd = s.Shift.GetAbsoluteEnd(sEs);
          var tz = DateTimeZoneProviders.Tzdb[sEs.TimeZoneId];
          var localStart = absStart.InZone(tz).LocalDateTime;
          var localEnd = absEnd.InZone(tz).LocalDateTime;
          return new UserSignupConflictItem(
              Date: localStart.Date,
              RotaName: s.Shift.Rota.Name,
              AbsoluteStart: absStart,
              AbsoluteEnd: absEnd,
              DisplayStart: localStart.TimeOfDay.ToString("HH:mm", CultureInfo.InvariantCulture),
              DisplayEnd: localEnd.TimeOfDay.ToString("HH:mm", CultureInfo.InvariantCulture));
      })
      .ToList();
  ```

  Add `using NodaTime;` and `using System.Globalization;` to the controller's usings if not present.

- [ ] **Step 4: Add `UserActiveSignups = userActiveSignupsForUi` to the model construction at line 238-260**

  ```csharp
  UserActiveSignups = userActiveSignupsForUi,
  ```

- [ ] **Step 5: Build to confirm compiles**

  Run: `dotnet build Humans.slnx -v quiet`
  Expected: build succeeds.

- [ ] **Step 6: Run tests to confirm nothing broke**

  Run: `dotnet test Humans.slnx -v quiet`
  Expected: all green.

- [ ] **Step 7: Commit**

  ```bash
  git add src/Humans.Web/Controllers/ShiftsController.cs src/Humans.Application/Services/Shifts/ShiftSignupService.cs src/Humans.Application/Interfaces/Shifts/IShiftSignupService.cs
  git commit -m "$(cat <<'EOF'
  feat(shifts): expose GetActiveSignupsForUserAsync via service + load for dashboard

  Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
  EOF
  )"
  ```

### Task 2.3: Pass `UserActiveSignups` and per-rota window map through to partial

**Files:**
- Modify: `src/Humans.Web/Views/Shifts/Index.cshtml` (lines ~364-377 and ~513)

- [ ] **Step 1: Update both partial-construction sites**

  At each `new BuildStrikeRotaTableViewModel { ... }` block, add:

  ```cshtml
  UserActiveSignups = Model.UserActiveSignups,
  RotaWindowsByDayOffset = rotaGroup.Shifts
      .Where(s => s.Shift.IsAllDay)
      .ToDictionary(
          s => s.Shift.DayOffset,
          s => new ShiftWindow(s.AbsoluteStart, s.AbsoluteEnd))
  ```

  `ShiftDisplayItem` (defined at `ShiftViewModels.cs:230`) already carries pre-computed `AbsoluteStart` / `AbsoluteEnd` — reuse those instead of recomputing via `GetAbsoluteStart(es)`.

  Note: `rotaGroup` is already in scope at both sites; verify by reading line 360-377 first. The second site at line ~513 may use a different inner-scope variable name — adapt to whatever's there. Don't blindly substitute.

- [ ] **Step 2: Add necessary `@using` directives at the top of `Index.cshtml`**

  If not already present:
  ```cshtml
  @using Humans.Web.Models
  @using NodaTime
  ```

  (Most `.cshtml` files in this repo already get common usings via `_ViewImports.cshtml`. Check first — only add if needed.)

- [ ] **Step 3: Build**

  Run: `dotnet build Humans.slnx -v quiet`
  Expected: build succeeds.

- [ ] **Step 4: Commit**

  ```bash
  git add src/Humans.Web/Views/Shifts/Index.cshtml
  git commit -m "$(cat <<'EOF'
  feat(shifts): pass conflict data through to BuildStrike partial

  Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
  EOF
  )"
  ```

### Task 2.4: Pass `skipConflicts: true` from the user-initiated POST

**Files:**
- Modify: `src/Humans.Web/Controllers/ShiftsController.cs:302`

- [ ] **Step 1: Update the call**

  Change:
  ```csharp
  var result = await _signupService.SignUpRangeAsync(user.Id, rotaId, startDayOffset, endDayOffset, isPrivileged: privileged);
  ```
  to:
  ```csharp
  var result = await _signupService.SignUpRangeAsync(user.Id, rotaId, startDayOffset, endDayOffset, isPrivileged: privileged, skipConflicts: true);
  ```

- [ ] **Step 2: Build + run tests**

  Run: `dotnet build Humans.slnx -v quiet && dotnet test Humans.slnx -v quiet`
  Expected: all green.

- [ ] **Step 3: Commit**

  ```bash
  git add src/Humans.Web/Controllers/ShiftsController.cs
  git commit -m "$(cat <<'EOF'
  feat(shifts): user-initiated SignUpRange now skips conflicts (no whole-range fail)

  Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
  EOF
  )"
  ```

---

## Chunk 3: View — modal markup, data emission, JS handler

**Goal:** Replace the direct submit with a Bootstrap modal that shows the chosen range, conflicts, and arrival reminder. Emit data inline as JSON for client-side conflict computation. Modal lives inside the form so submission is the natural form-submit path.

**Note on JS:** The handler is small (~50 lines). Per the spec it stays inline; if it grows past that during implementation, extract to `wwwroot/js/shifts-range-confirm.js`. Decision deferred until the actual code is written.

### Task 3.1: Switch button to modal trigger; add modal skeleton

**Files:**
- Modify: `src/Humans.Web/Views/Shared/_BuildStrikeRotaTable.cshtml:38-41`

- [ ] **Step 1: Change submit button to modal trigger**

  Replace lines 38-40:
  ```cshtml
  <div class="col-auto">
      <button type="submit" class="btn btn-sm btn-success">@string.Format(Localizer["Shifts_SignUpForDates"].Value, rotaGroup.Rota.Period == RotaPeriod.Build ? Localizer["Shifts_SetUp"].Value.ToLowerInvariant() : Localizer["Shifts_Strike"].Value.ToLowerInvariant())</button>
  </div>
  ```
  with:
  ```cshtml
  <div class="col-auto">
      <button type="button" class="btn btn-sm btn-success js-open-confirm-signup"
              data-bs-toggle="modal" data-bs-target="#confirmSignup-@rotaGroup.Rota.Id">
          @string.Format(Localizer["Shifts_SignUpForDates"].Value, rotaGroup.Rota.Period == RotaPeriod.Build ? Localizer["Shifts_SetUp"].Value.ToLowerInvariant() : Localizer["Shifts_Strike"].Value.ToLowerInvariant())
      </button>
  </div>
  ```

- [ ] **Step 2: Append modal markup inside the form, after the button div but before `</form>`**

  ```cshtml
  <div class="modal fade" id="confirmSignup-@rotaGroup.Rota.Id" tabindex="-1" aria-hidden="true">
      <div class="modal-dialog">
          <div class="modal-content">
              <div class="modal-header">
                  <h5 class="modal-title">
                      @string.Format(Localizer["Shifts_ConfirmSignup_Title"].Value,
                          rotaGroup.Rota.Period == RotaPeriod.Build ? Localizer["Shifts_SetUp"].Value.ToLowerInvariant() : Localizer["Shifts_Strike"].Value.ToLowerInvariant())
                  </h5>
                  <button type="button" class="btn-close" data-bs-dismiss="modal" aria-label="Close"></button>
              </div>
              <div class="modal-body">
                  <p class="js-phase-line">
                      @string.Format(Localizer["Shifts_ConfirmSignup_Phase"].Value,
                          rotaGroup.Rota.Period == RotaPeriod.Build ? Localizer["Shifts_SetUp"].Value.ToLowerInvariant() : Localizer["Shifts_Strike"].Value.ToLowerInvariant())
                  </p>
                  <p class="js-range-line"></p>

                  <div class="alert alert-info d-none js-conflicts-partial">
                      <strong>@Localizer["Shifts_ConfirmSignup_Conflicts_Heading"]</strong>
                      <ul class="mb-2 js-conflicts-list"></ul>
                      <small class="text-muted">@Localizer["Shifts_ConfirmSignup_Conflicts_PartialNote"]</small>
                  </div>

                  <div class="alert alert-warning d-none js-conflicts-all">
                      @Localizer["Shifts_ConfirmSignup_Conflicts_AllBlocked"]
                  </div>

                  <div class="alert alert-warning js-arrival-callout">
                      <i class="fa-solid fa-circle-info me-1"></i>
                      <span class="js-arrive-by-line"></span>
                  </div>

                  <p class="text-muted small mb-0">@Localizer["Shifts_ConfirmSignup_Prompt"]</p>
              </div>
              <div class="modal-footer">
                  <button type="button" class="btn btn-secondary" data-bs-dismiss="modal">@Localizer["Common_Cancel"]</button>
                  <button type="submit" class="btn btn-success js-confirm-submit">@Localizer["Shifts_ConfirmSignup_Confirm"]</button>
              </div>
          </div>
      </div>
  </div>
  ```

- [ ] **Step 3: Build**

  Run: `dotnet build Humans.slnx -v quiet`
  Expected: build succeeds. (Localizer keys are referenced but not yet defined — at runtime the Localizer returns the key name as a fallback, so the page won't crash. Tests still pass because no test exercises this view.)

- [ ] **Step 4: Commit**

  ```bash
  git add src/Humans.Web/Views/Shared/_BuildStrikeRotaTable.cshtml
  git commit -m "$(cat <<'EOF'
  feat(shifts): add confirmation modal markup to BuildStrike range form

  Modal lives inside the form so the success button posts naturally.
  Localizer keys added in a follow-up commit.

  Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
  EOF
  )"
  ```

### Task 3.2: Emit data attributes and JSON blobs

**Files:**
- Modify: `src/Humans.Web/Views/Shared/_BuildStrikeRotaTable.cshtml`

- [ ] **Step 1: Add `data-date` and `data-arrive-by` to each `<option>`**

  In the start-day select (lines 22-27), change:
  ```cshtml
  <option value="@s.Shift.DayOffset">@es.GateOpeningDate.PlusDays(s.Shift.DayOffset).ToDisplayShiftDate()</option>
  ```
  to:
  ```cshtml
  <option value="@s.Shift.DayOffset"
          data-date="@es.GateOpeningDate.PlusDays(s.Shift.DayOffset).ToDisplayShiftDate()"
          data-arrive-by="@es.GateOpeningDate.PlusDays(s.Shift.DayOffset - 1).ToDisplayShiftDate()">
      @es.GateOpeningDate.PlusDays(s.Shift.DayOffset).ToDisplayShiftDate()
  </option>
  ```

  In the end-day select (lines 31-36), change to:
  ```cshtml
  <option value="@s.Shift.DayOffset"
          data-date="@es.GateOpeningDate.PlusDays(s.Shift.DayOffset).ToDisplayShiftDate()"
          selected="@(s == availableShifts.Last() ? "selected" : null)">
      @es.GateOpeningDate.PlusDays(s.Shift.DayOffset).ToDisplayShiftDate()
  </option>
  ```

- [ ] **Step 2: Append two `<script type="application/json">` blocks inside the form, right before the modal markup added in Task 3.1**

  ```cshtml
  <script type="application/json" class="js-user-signups-data">
      @Html.Raw(System.Text.Json.JsonSerializer.Serialize(
          Model.UserActiveSignups.Select(u => new {
              date = u.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
              rota = u.RotaName,
              absStart = u.AbsoluteStart.ToString(),
              absEnd = u.AbsoluteEnd.ToString(),
              displayStart = u.DisplayStart,
              displayEnd = u.DisplayEnd
          })))
  </script>
  <script type="application/json" class="js-rota-windows-data">
      @Html.Raw(System.Text.Json.JsonSerializer.Serialize(
          Model.RotaWindowsByDayOffset.ToDictionary(
              kv => kv.Key.ToString(System.Globalization.CultureInfo.InvariantCulture),
              kv => new { absStart = kv.Value.AbsoluteStart.ToString(), absEnd = kv.Value.AbsoluteEnd.ToString() })))
  </script>
  ```

  Notes:
  - `Instant.ToString()` produces ISO-8601 UTC ("2026-05-06T10:00:00Z") which is lexicographically comparable — that's how the JS overlap check works.
  - `@Html.Raw` is safe here because `JsonSerializer` produces JSON that's safe inside a `<script type="application/json">` block (browsers don't HTML-decode the contents).

- [ ] **Step 3: Build to confirm Razor compiles**

  Run: `dotnet build Humans.slnx -v quiet`
  Expected: build succeeds.

- [ ] **Step 4: Commit**

  ```bash
  git add src/Humans.Web/Views/Shared/_BuildStrikeRotaTable.cshtml
  git commit -m "$(cat <<'EOF'
  feat(shifts): emit conflict data as inline JSON in BuildStrike partial

  Per-form JSON blobs (user's cross-event signups, this rota's per-day
  windows) plus data-date / data-arrive-by attributes on options give
  the modal handler everything it needs without extra round-trips.

  Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
  EOF
  )"
  ```

### Task 3.3: Inline JS handler — populate modal and detect conflicts

**Files:**
- Modify: `src/Humans.Web/Views/Shared/_BuildStrikeRotaTable.cshtml` (append before closing `}` of the `@if (availableShifts.Count > 0)` block, OR at the bottom of the partial scoped per-rota)

- [ ] **Step 1: Add inline `<script>` block at the very bottom of the partial (so it runs once per render)**

  ```cshtml
  <script>
  (function () {
      var modalEl = document.getElementById('confirmSignup-@rotaGroup.Rota.Id');
      if (!modalEl) return;
      var form = modalEl.closest('form');
      if (!form) return;

      var userSignups = JSON.parse(form.querySelector('.js-user-signups-data').textContent || '[]');
      var rotaWindows = JSON.parse(form.querySelector('.js-rota-windows-data').textContent || '{}');
      var startSel = form.querySelector('select[name="startDayOffset"]');
      var endSel = form.querySelector('select[name="endDayOffset"]');

      function fmt(tpl, args) {
          // Replace ALL occurrences of {0}, {1}, ... — guards future translations
          // that might repeat a placeholder. JS String.replace replaces first only.
          return Object.keys(args).reduce(function (s, k) {
              return s.split('{' + k + '}').join(String(args[k]));
          }, tpl);
      }

      function offsetsBetween(a, b) {
          var lo = Math.min(a, b), hi = Math.max(a, b), out = [];
          for (var i = lo; i <= hi; i++) out.push(i);
          return out;
      }

      function findConflictsForOffset(offset) {
          var win = rotaWindows[offset];
          if (!win) return [];
          // overlap rule: existing.absStart < win.absEnd && existing.absEnd > win.absStart
          // string comparison works on ISO-8601 UTC strings.
          return userSignups.filter(function (s) {
              return s.absStart < win.absEnd && s.absEnd > win.absStart;
          });
      }

      modalEl.addEventListener('show.bs.modal', function () {
          var startOpt = startSel.options[startSel.selectedIndex];
          var endOpt = endSel.options[endSel.selectedIndex];
          var startOffset = parseInt(startSel.value, 10);
          var endOffset = parseInt(endSel.value, 10);

          // Normalise: if user picked start > end, swap for display purposes.
          // (Server still receives the raw values; the inverted-range case is rare.)
          var lo = Math.min(startOffset, endOffset), hi = Math.max(startOffset, endOffset);
          var days = hi - lo + 1;

          var startDate = startOpt.getAttribute('data-date');
          var endDate = endOpt.getAttribute('data-date');
          var arriveBy = startOpt.getAttribute('data-arrive-by');

          // Range line (use the localised template — emitted as a data attr on the modal body)
          var rangeTpl = modalEl.getAttribute('data-range-tpl-multi') || '{0} – {1} ({2} days)';
          var singleTpl = modalEl.getAttribute('data-range-tpl-single') || '{0} (1 day)';
          var rangeLine = days === 1
              ? fmt(singleTpl, { 0: startDate })
              : fmt(rangeTpl, { 0: startDate, 1: endDate, 2: days });
          modalEl.querySelector('.js-range-line').textContent = rangeLine;

          // Arrival callout
          var arriveTpl = modalEl.getAttribute('data-arrive-tpl') || "You'll be expected on site by {0}.";
          modalEl.querySelector('.js-arrive-by-line').textContent = fmt(arriveTpl, { 0: arriveBy });

          // Compute conflicts
          var allConflicts = [];
          for (var off = lo; off <= hi; off++) {
              var conflicts = findConflictsForOffset(off);
              if (conflicts.length > 0) {
                  // We need the calendar date for that offset — use the option's data-date
                  var opt = startSel.querySelector('option[value="' + off + '"]')
                          || endSel.querySelector('option[value="' + off + '"]');
                  var dateLabel = opt ? opt.getAttribute('data-date') : String(off);
                  conflicts.forEach(function (c) {
                      allConflicts.push({ date: dateLabel, conflict: c });
                  });
              }
          }

          var partialAlert = modalEl.querySelector('.js-conflicts-partial');
          var allBlockedAlert = modalEl.querySelector('.js-conflicts-all');
          var list = modalEl.querySelector('.js-conflicts-list');
          var submitBtn = modalEl.querySelector('.js-confirm-submit');

          partialAlert.classList.add('d-none');
          allBlockedAlert.classList.add('d-none');
          list.innerHTML = '';
          submitBtn.removeAttribute('disabled');

          if (allConflicts.length > 0) {
              // Determine if every day in range is conflicted
              var conflictedOffsets = new Set();
              for (var off2 = lo; off2 <= hi; off2++) {
                  if (findConflictsForOffset(off2).length > 0) conflictedOffsets.add(off2);
              }
              var rowTpl = modalEl.getAttribute('data-conflict-row-tpl')
                  || '{0} — already signed up for {1} ({2}–{3})';

              if (conflictedOffsets.size === days) {
                  allBlockedAlert.classList.remove('d-none');
                  submitBtn.setAttribute('disabled', 'disabled');
              } else {
                  allConflicts.forEach(function (c) {
                      var li = document.createElement('li');
                      li.textContent = fmt(rowTpl, {
                          0: c.date,
                          1: c.conflict.rota,
                          2: c.conflict.displayStart,
                          3: c.conflict.displayEnd
                      });
                      list.appendChild(li);
                  });
                  partialAlert.classList.remove('d-none');
              }
          }
      });
  })();
  </script>
  ```

- [ ] **Step 2: Add localised template strings as `data-*` attributes on the modal element**

  In the modal markup added in Task 3.1, change the modal opening tag from:
  ```cshtml
  <div class="modal fade" id="confirmSignup-@rotaGroup.Rota.Id" tabindex="-1" aria-hidden="true">
  ```
  to:
  ```cshtml
  <div class="modal fade" id="confirmSignup-@rotaGroup.Rota.Id" tabindex="-1" aria-hidden="true"
       data-range-tpl-multi="@Localizer["Shifts_ConfirmSignup_Range"]"
       data-range-tpl-single="@Localizer["Shifts_ConfirmSignup_Range_Single"]"
       data-arrive-tpl="@Localizer["Shifts_ConfirmSignup_ArriveBy"]"
       data-conflict-row-tpl="@Localizer["Shifts_ConfirmSignup_Conflicts_Row"]">
  ```

- [ ] **Step 3: Build**

  Run: `dotnet build Humans.slnx -v quiet`
  Expected: build succeeds.

- [ ] **Step 4: Smoke test in browser**

  Run: `dotnet run --project src/Humans.Web` (in another terminal).

  Navigate to `/Shifts` as a logged-in user with at least one Build/Strike rota in scope.

  Verify:
  - Click "Sign up for setup/strike" → modal opens.
  - Modal shows: phase line, "From {start} to {end} (N days)", arrival callout with "expected on site by {start − 1 day}".
  - "Cancel" closes the modal with no POST (check Network tab).
  - "Confirm sign-up" submits the form and redirects to the dashboard with a success toast.
  - If the user has another active signup whose date+time overlaps a day in the range, the conflicts section shows it.
  - If the user's range is entirely conflicted, the confirm button is disabled.

  No commit yet — testing only. If anything's broken, fix before continuing.

- [ ] **Step 5: Commit**

  ```bash
  git add src/Humans.Web/Views/Shared/_BuildStrikeRotaTable.cshtml
  git commit -m "$(cat <<'EOF'
  feat(shifts): wire up confirm-modal handler with cross-rota conflict surfacing

  The handler reads the inline JSON blobs on show.bs.modal, computes
  per-day overlap against the user's active signups using ISO-8601 UTC
  string comparison, and renders one of three states (no conflicts,
  partial, all-blocked).

  Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
  EOF
  )"
  ```

---

## Chunk 4: Localization

**Goal:** Add the new `Shifts_ConfirmSignup_*` keys (and `Common_Cancel` if missing) to all six locale resx files. All six locales hand-translated to match the existing pattern in this codebase.

### Task 4.1: Inventory existing keys and pattern

**Files (read only):**
- `src/Humans.Web/Resources/SharedResource.resx`
- `src/Humans.Web/Resources/SharedResource.de.resx`
- `src/Humans.Web/Resources/SharedResource.es.resx`
- `src/Humans.Web/Resources/SharedResource.ca.resx`
- `src/Humans.Web/Resources/SharedResource.fr.resx`
- `src/Humans.Web/Resources/SharedResource.it.resx`

- [ ] **Step 1: Verify `Common_Cancel` exists in all six files**

  Run: `grep -l 'name="Common_Cancel"' src/Humans.Web/Resources/SharedResource*.resx`
  Expected: all six files. If any are missing it, add the standard "Cancel"/"Cancelar"/etc.

- [ ] **Step 2: Note the pattern for adding new keys**

  resx XML format:
  ```xml
  <data name="Key_Name" xml:space="preserve">
    <value>The localised text</value>
  </data>
  ```

  Insert in alphabetical-by-key order if the file appears alphabetised; otherwise append before `</root>`. Match what's already there.

  Locale pattern (verified at first-pass review): all four non-default locales (de, ca, fr, it) hand-translate their `Shifts_*` keys. No English fallbacks, no `<!-- TODO -->` markers. Task 4.4 follows that pattern.

  No commit — orientation only.

### Task 4.2: Add keys to English (default) resx

**Files:**
- Modify: `src/Humans.Web/Resources/SharedResource.resx`

- [ ] **Step 1: Add the eleven new keys**

  | Key | Value |
  |---|---|
  | `Shifts_ConfirmSignup_Title` | `Confirm your {0} sign-up` |
  | `Shifts_ConfirmSignup_Phase` | `You're signing up for the {0} phase.` |
  | `Shifts_ConfirmSignup_Range` | `From {0} to {1} ({2} days).` |
  | `Shifts_ConfirmSignup_Range_Single` | `For {0} (1 day).` |
  | `Shifts_ConfirmSignup_ArriveBy` | `You'll be expected on site by {0}.` |
  | `Shifts_ConfirmSignup_Prompt` | `Is this the period you intended to sign up for?` |
  | `Shifts_ConfirmSignup_Confirm` | `Confirm sign-up` |
  | `Shifts_ConfirmSignup_Conflicts_Heading` | `Some days in this range conflict with shifts you're already signed up for:` |
  | `Shifts_ConfirmSignup_Conflicts_Row` | `{0} — already signed up for {1} ({2}–{3})` |
  | `Shifts_ConfirmSignup_Conflicts_PartialNote` | `Sign-up will only add days that don't conflict.` |
  | `Shifts_ConfirmSignup_Conflicts_AllBlocked` | `Every day in this range conflicts with existing signups — nothing to add.` |

  If `Common_Cancel` is missing from the default resx (unlikely), add it with value `Cancel`.

- [ ] **Step 2: Build**

  Run: `dotnet build Humans.slnx -v quiet`
  Expected: build succeeds.

- [ ] **Step 3: Manual smoke test in browser**

  Reload `/Shifts`, open the modal — text should now render in English.

- [ ] **Step 4: Commit**

  ```bash
  git add src/Humans.Web/Resources/SharedResource.resx
  git commit -m "$(cat <<'EOF'
  i18n(shifts): add ConfirmSignup_* keys (English)

  Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
  EOF
  )"
  ```

### Task 4.3: Add Spanish translations

**Files:**
- Modify: `src/Humans.Web/Resources/SharedResource.es.resx`

- [ ] **Step 1: Add hand-translated Spanish values**

  | Key | Value |
  |---|---|
  | `Shifts_ConfirmSignup_Title` | `Confirma tu inscripción al {0}` |
  | `Shifts_ConfirmSignup_Phase` | `Te vas a inscribir en la fase de {0}.` |
  | `Shifts_ConfirmSignup_Range` | `Del {0} al {1} ({2} días).` |
  | `Shifts_ConfirmSignup_Range_Single` | `Para el {0} (1 día).` |
  | `Shifts_ConfirmSignup_ArriveBy` | `Se espera que estés en el sitio antes del {0}.` |
  | `Shifts_ConfirmSignup_Prompt` | `¿Es este el periodo en el que querías inscribirte?` |
  | `Shifts_ConfirmSignup_Confirm` | `Confirmar inscripción` |
  | `Shifts_ConfirmSignup_Conflicts_Heading` | `Algunos días de este rango entran en conflicto con turnos en los que ya estás inscrito/a:` |
  | `Shifts_ConfirmSignup_Conflicts_Row` | `{0} — ya inscrito/a en {1} ({2}–{3})` |
  | `Shifts_ConfirmSignup_Conflicts_PartialNote` | `Solo se añadirán los días que no entren en conflicto.` |
  | `Shifts_ConfirmSignup_Conflicts_AllBlocked` | `Todos los días de este rango entran en conflicto con inscripciones existentes — no hay nada que añadir.` |

- [ ] **Step 2: Build**

  Run: `dotnet build Humans.slnx -v quiet`
  Expected: build succeeds.

- [ ] **Step 3: Commit**

  ```bash
  git add src/Humans.Web/Resources/SharedResource.es.resx
  git commit -m "$(cat <<'EOF'
  i18n(shifts): add ConfirmSignup_* keys (Spanish)

  Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
  EOF
  )"
  ```

### Task 4.4: Add translations for de/ca/fr/it

**Files:**
- Modify: `SharedResource.de.resx`, `SharedResource.ca.resx`, `SharedResource.fr.resx`, `SharedResource.it.resx`

**Existing pattern (verified):** all four files contain hand-translated values for the `Shifts_*` keys — no English fallbacks, no `<!-- TODO -->` markers. Match that pattern: provide a credible best-effort hand-translation per locale. Don't agonise; strings are short and easy to refine in a follow-up i18n pass if needed.

**Translator notes for every step in this task:**
- Preserve every `{0}`, `{1}`, etc. placeholder verbatim — they are runtime arguments. Do not translate or remove them.
- Match the lower-case `montaje`/`desmontaje`-style noun usage if mirroring Spanish; for the other languages use whatever existing pattern is in place.

- [ ] **Step 1: Add the eleven hand-translated keys to `SharedResource.de.resx` (German)**

  Build after editing this single file:

  ```bash
  dotnet build Humans.slnx -v quiet
  ```
  Expected: build succeeds (catches resx XML schema errors per-file rather than after all four).

- [ ] **Step 2: Add the eleven hand-translated keys to `SharedResource.ca.resx` (Catalan)**

  Build after editing:
  ```bash
  dotnet build Humans.slnx -v quiet
  ```
  Expected: build succeeds.

- [ ] **Step 3: Add the eleven hand-translated keys to `SharedResource.fr.resx` (French)**

  Build:
  ```bash
  dotnet build Humans.slnx -v quiet
  ```
  Expected: build succeeds.

- [ ] **Step 4: Add the eleven hand-translated keys to `SharedResource.it.resx` (Italian)**

  Build:
  ```bash
  dotnet build Humans.slnx -v quiet
  ```
  Expected: build succeeds.

- [ ] **Step 5: Commit (one commit covering all four files)**

  ```bash
  git add src/Humans.Web/Resources/SharedResource.de.resx src/Humans.Web/Resources/SharedResource.ca.resx src/Humans.Web/Resources/SharedResource.fr.resx src/Humans.Web/Resources/SharedResource.it.resx
  git commit -m "$(cat <<'EOF'
  i18n(shifts): add ConfirmSignup_* keys (de, ca, fr, it)

  Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
  EOF
  )"
  ```

---

## Final verification

- [ ] **Step 1: Build clean**

  Run: `dotnet build Humans.slnx -v quiet`
  Expected: 0 warnings, 0 errors.

- [ ] **Step 2: Full test suite**

  Run: `dotnet test Humans.slnx -v quiet`
  Expected: 0 failures.

- [ ] **Step 3: Manual browser smoke test**

  Run: `dotnet run --project src/Humans.Web`

  As a logged-in user with at least one Build/Strike rota visible:
  1. With no other active signups: open modal → no conflicts section → confirm creates signups → success toast.
  2. Sign up for a non-overlapping shift in another rota → reopen modal → still no conflicts.
  3. Sign up for a shift whose time overlaps one of the days in range → reopen modal → conflicts section lists that day → confirm creates signups for the rest of the range and shows a warning toast about the skipped day.
  4. Manually craft a range where every day conflicts → confirm button is disabled.
  5. Cancel button closes modal with no POST (verify in Network tab).
  6. Open modal, change start/end dropdowns, reopen → modal reflects the new range.
  7. Switch UI language to Spanish (or whichever locale you can set) → all modal text renders in that locale.

- [ ] **Step 4: If anything in Step 3 fails, fix and commit.**

- [ ] **Step 5: Push branch and open PR**

  Branch is `shifts-dashboard-setup-subfilter` (feature work continues on the existing branch — the brainstorm/spec/plan commits are part of the same effort). PR title: `feat(shifts): confirmation modal for Build/Strike range signup`.

  Body should reference the spec at `docs/superpowers/specs/2026-05-04-shift-range-signup-confirmation-design.md` and call out the user-facing change ("misclicks no longer commit you to multi-day ranges; the modal previews dates, day count, on-site arrival, and existing-signup conflicts before submission").

  Per CLAUDE.md, this is a feature branch → PR to `main` on peter's fork (origin), squash-merge unless commits are already a clean linear story. The chunk-by-chunk commits above are intentionally readable so the PR can land as a single squash commit or be left as-is at peter's discretion.
