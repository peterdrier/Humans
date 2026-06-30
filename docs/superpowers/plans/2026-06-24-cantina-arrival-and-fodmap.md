# Cantina arrival-day feeding + remove FODMAP — Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove the FODMAP intolerance option, and make the Cantina roster feed each human on the day before their first *confirmed* shift.

**Architecture:** Two independent features → **two branches, two PRs** off `origin/main`. Feature 1 is a deletion across the canonical option list + locale files. Feature 2 flips one hardcoded scope constant in the Shifts service (cantina is its sole caller) and adds an inline "earliest confirmed day" scan in `CantinaRosterService` to derive each human's arrival day (`firstConfirmedOffset − 1`).

**Tech Stack:** .NET (C#), EF Core, NodaTime, xUnit (`[HumansFact]`), NSubstitute, AwesomeAssertions.

**Spec:** `docs/superpowers/specs/2026-06-24-cantina-arrival-and-fodmap-design.md`

**Build/test (from repo root):** `dotnet build Humans.slnx -v quiet` · `dotnet test Humans.slnx -v quiet` (the `-v quiet` is mandatory).

---

## Chunk 1: PR #1 — Remove FODMAP intolerance

Branch: `chore/remove-fodmap-intolerance` (worktree `.worktrees/fodmap-removal`), off `origin/main`.
This is a near-mechanical deletion; one test changes. No migration (existing data left inert, per spec).

### Task 1.1: Drop FODMAP from the canonical list (red via existing test)

**Files:**
- Modify: `src/Humans.Domain/Constants/DietaryOptions.cs:38`
- Modify (test): `tests/Humans.Application.Tests/Services/Cantina/CantinaRosterServiceTests.cs:307`

- [ ] **Step 1: Update the existing rollup test to the new expectation (it should fail first).**
  In `CantinaRosterServiceTests.cs` remove the now-invalid assertion line:
  ```csharp
  intolerance["FODMAP"].Should().Be(0);
  ```
  Removing it is required because, once FODMAP leaves `IntoleranceOptions`, the rollup dictionary
  no longer contains a `"FODMAP"` key and that line would throw `KeyNotFoundException`.

- [ ] **Step 2: Run the affected test BEFORE the domain change to confirm current green baseline.**
  Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~CantinaRosterServiceTests"`
  Expected: PASS (baseline, FODMAP still present).

- [ ] **Step 3: Remove `"FODMAP"` from `IntoleranceOptions`.**
  `DietaryOptions.cs:38`:
  ```csharp
  // before
  public static readonly IReadOnlyList<string> IntoleranceOptions =
      ["Lactose", "Gluten", "Histamine", "FODMAP", OtherOption];
  // after
  public static readonly IReadOnlyList<string> IntoleranceOptions =
      ["Lactose", "Gluten", "Histamine", OtherOption];
  ```

- [ ] **Step 4: Run the cantina tests again.**
  Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~CantinaRosterServiceTests"`
  Expected: PASS (rollup no longer has FODMAP; assertion removed).

- [ ] **Step 5: Commit.**
  ```bash
  git add src/Humans.Domain/Constants/DietaryOptions.cs \
          tests/Humans.Application.Tests/Services/Cantina/CantinaRosterServiceTests.cs
  git commit -m "feat(profile): remove FODMAP from intolerance options"
  ```

### Task 1.2: Delete the FODMAP localization entries

**Files (modify all six):**
`src/Humans.Web/Resources/SharedResource.resx`, `.es.resx`, `.de.resx`, `.ca.resx`, `.fr.resx`, `.it.resx`

- [ ] **Step 1: Delete the `Profile_DietaryMedical_Intolerance_FODMAP` `<data>` element from each file.**
  Each file has exactly one line of the form:
  ```xml
  <data name="Profile_DietaryMedical_Intolerance_FODMAP" xml:space="preserve"><value>FODMAP</value></data>
  ```
  Remove that single line in all six files. One-liner per file:
  ```bash
  for f in SharedResource SharedResource.es SharedResource.de SharedResource.ca SharedResource.fr SharedResource.it; do
    sed -i '/name="Profile_DietaryMedical_Intolerance_FODMAP"/d' "src/Humans.Web/Resources/$f.resx"
  done
  ```

- [ ] **Step 2: Verify no FODMAP references remain anywhere.**
  Run: `grep -rni "FODMAP" src/ tests/`
  Expected: no output.

- [ ] **Step 3: Build to confirm resx still well-formed and nothing references the dropped key.**
  Run: `dotnet build Humans.slnx -v quiet`
  Expected: 0 errors.

- [ ] **Step 4: Full test run.**
  Run: `dotnet test Humans.slnx -v quiet`
  Expected: PASS.

- [ ] **Step 5: Commit.**
  ```bash
  git add src/Humans.Web/Resources/SharedResource*.resx
  git commit -m "chore(i18n): drop FODMAP intolerance label from all locales"
  ```

### Task 1.3: Open PR #1
- [ ] Push branch to `origin` and open a PR to `origin/main` titled
  `feat(profile): remove FODMAP from intolerances`. Body: link the spec; note existing stored
  `"FODMAP"` values are left inert (no migration) and self-heal on next profile save.

---

## Chunk 2: PR #2 — Cantina arrival-day feeding

Branch: `feat/cantina-arrival-day` (this worktree; carries the spec + plan docs), off `origin/main`.
Confirmed-only scope change + inline arrival-day computation. TDD against `CantinaRosterService`.

> **Mock note:** `CantinaRosterServiceTests` stub `IShiftManagementService.GetOnSiteUserIdsForDayAsync`
> at the interface (which has no scope parameter). So the §(a) scope-constant change is invisible to
> these unit tests — they assert on whatever the stub returns. Arrival-day tests must therefore stub
> the *range* of offsets the helper scans, which means setting event offsets on the fixture (next note).

> **Fixture note:** `ActiveEvent()` currently sets no `BuildStartOffset`/`StrikeEndOffset` (both default
> 0), so the (b) range scan would cover only day 0. Arrival-day tests must build an event with explicit
> offsets wide enough to cover the days they stub, e.g. `BuildStartOffset = -2, StrikeEndOffset = 8`.

### Task 2.1: Confirmed-only scope + doc comments (no cantina-test impact)

**Files:**
- Modify: `src/Humans.Application/Services/Shifts/ShiftManagementService.cs:1979`
- Modify (doc): `src/Humans.Application/Interfaces/Shifts/IShiftManagementService.cs:354`

- [ ] **Step 1: Flip the scope constant.**
  `ShiftManagementService.cs:1979`: `ShiftDayUserStatusScope.PendingOrConfirmed` → `ShiftDayUserStatusScope.ConfirmedOnly`.

- [ ] **Step 2: Fix the now-stale XML doc on the interface method** (`IShiftManagementService.cs:354`):
  change wording "Pending/Confirmed signup" → "Confirmed signup". (The stale wording lives only here
  and in `Cantina.md` — there is no prose comment at the impl site 1974-1980, so don't go hunting.)

- [ ] **Step 3: Build + run shifts/cantina tests (should stay green — cantina mocks this method).**
  Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~Cantina|FullyQualifiedName~ShiftManagement"`
  Expected: PASS.

- [ ] **Step 4: Commit.**
  ```bash
  git add src/Humans.Application/Services/Shifts/ShiftManagementService.cs \
          src/Humans.Application/Interfaces/Shifts/IShiftManagementService.cs
  git commit -m "feat(cantina): count only confirmed shifts for on-site cohort"
  ```

### Task 2.2: Earliest-confirmed-day helper + weekly arrival day (TDD)

**Files:**
- Modify: `src/Humans.Application/Services/Cantina/CantinaRosterService.cs`
- Test: `tests/Humans.Application.Tests/Services/Cantina/CantinaRosterServiceTests.cs`

- [ ] **Step 1: Write the failing test — first shift Wed ⇒ appears Tue as arrival.**
  Build an event with offsets covering the days, stub confirmed cohorts so a human's earliest day is
  Wednesday (offset 2), and assert they now show on Tuesday (offset 1) with `ArrivesOn` = Tue and Tue
  in `NoShift`. Sketch:
  ```csharp
  [HumansFact]
  public async Task GetWeeklyRoster_FeedsHumanDayBeforeFirstConfirmedShift()
  {
      var ev = ActiveEventWithRange(buildStart: -2, strikeEnd: 8); // helper sets offsets
      _shiftMgmt.GetActiveAsync().Returns(ev);
      var id = Guid.NewGuid();
      // First (and only) confirmed shift on Wednesday = offset 2.
      _shiftMgmt.GetOnSiteUserIdsForDayAsync(ev.Id, 2, Arg.Any<CancellationToken>())
          .Returns(Task.FromResult<IReadOnlyList<Guid>>(new[] { id }));
      SetupHumans(Human(id, "Ash"));

      var result = await _service.GetWeeklyRosterAsync(0, TestContext.Current.CancellationToken);

      var person = result.People.Single(p => p.UserId == id);
      person.ArrivesOn.Should().Be(GateOpening.PlusDays(1));        // Tuesday
      person.NoShift.Should().Contain(GateOpening.PlusDays(1));      // present, no shift
      result.Days[1].TotalOnSite.Should().Be(1);                    // Tuesday cohort includes them
  }
  ```
  Add a small `ActiveEventWithRange(...)` fixture helper (mirrors `ActiveEvent()` + sets
  `BuildStartOffset`/`StrikeEndOffset`/`EventEndOffset`). Reuse the existing `SetupHumans`/`Human` helpers.

- [ ] **Step 2: Run it — expect FAIL** (person absent Tuesday today).
  Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~FeedsHumanDayBeforeFirstConfirmedShift"`
  Expected: FAIL.

- [ ] **Step 3: Implement.**
  In `CantinaRosterService`:
  - Add `private async Task<Dictionary<Guid,int>> BuildFirstConfirmedOffsetByUserAsync(EventSettings ev, CancellationToken ct)`
    looping `offset = ev.BuildStartOffset; offset <= ev.StrikeEndOffset; offset++`, calling
    `_shiftMgmt.GetOnSiteUserIdsForDayAsync(ev.Id, offset, ct)`, recording min offset per user.
  - In `GetWeeklyRosterAsync`, after `BuildDaysOnSiteByUserId(...)`, call the helper and for each
    `(user, minOffset)` compute `arrivalOffset = minOffset - 1`; if `weekStartDate.PlusDays(arrivalOffset - weekStartOffset)`
    falls within `weekDays`, add that date to the user's list in `daysOnSiteByUserId` (creating the
    entry if absent), then recompute `uniqueUserIds = daysOnSiteByUserId.Keys`. Existing downstream
    code (`ArrivesOn = daysList[0]` after sort, `NoShift`, aggregates over `uniqueUserIds`) then needs
    no further change.

- [ ] **Step 4: Run the test — expect PASS.**
  Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~FeedsHumanDayBeforeFirstConfirmedShift"`
  Expected: PASS.

- [ ] **Step 5: Commit.**
  ```bash
  git add src/Humans.Application/Services/Cantina/CantinaRosterService.cs \
          tests/Humans.Application.Tests/Services/Cantina/CantinaRosterServiceTests.cs
  git commit -m "feat(cantina): feed humans the day before their first confirmed shift (weekly)"
  ```

### Task 2.3: Weekly edge cases (TDD, one test each)

For each, write the failing test, run (some may already pass given 2.2's impl — if so, keep as
regression guards), then commit. Add per behavior:

- [ ] **Arrival-only human pulled into the week.** First confirmed shift = Monday of next week
  (offset 7); assert they appear on this week's Sunday (offset 6) even with no in-week shift, counted
  once in `TotalUniqueOnSite`/dietary/rollups.
- [ ] **No clamp.** Render the *previous* week (`GetWeeklyRosterAsync(weekStartOffset: -7)`) with an
  event whose `BuildStartOffset` ≤ −1; stub a human's first confirmed shift on offset 0; assert their
  arrival day (offset −1, the Sunday of week −7's window) is present and equals `ArrivesOn`, proving
  the negative/pre-event arrival day is not clamped away.
- [ ] **Global-minimum guard.** Human has a confirmed shift in a *prior* week and again this week;
  assert NO spurious arrival day is added this week (their min offset is in the earlier week).
- [ ] **Commit** the edge-case tests:
  ```bash
  git commit -am "test(cantina): weekly arrival-day edge cases (arrival-only, no-clamp, global-min)"
  ```

### Task 2.4: Daily drill-down arrival day (TDD)

**Files:** `CantinaRosterService.cs` (`GetDailyRosterAsync`), `CantinaDailyRosterServiceTests.cs`

- [ ] **Step 1: Failing test** — day N includes humans whose first confirmed shift is N+1, and
  excludes pending-only (mock returns confirmed cohort only). Assert such a human appears in the
  day-N `People` and counts in that day's aggregates.

- [ ] **Step 2: Run — expect FAIL.**
  Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~CantinaDailyRosterServiceTests"`
  Expected: FAIL (arrival human absent on day N).

- [ ] **Step 3: Implement.** In `GetDailyRosterAsync(dayOffset = N)`, after fetching the day-N cohort,
  call `BuildFirstConfirmedOffsetByUserAsync` and union in users whose `minOffset == N + 1`. Add them
  to the `userIds` set before building people/aggregates (dedup via set semantics).

- [ ] **Step 4: Run — expect PASS.**
  Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~CantinaDailyRosterServiceTests"`
  Expected: PASS.

- [ ] **Step 5: Commit.**
  ```bash
  git commit -am "feat(cantina): include next-day arrivals in daily drill-down"
  ```

### Task 2.5: CSV parity check + full suite + docs

- [ ] **Step 1:** Confirm the CSV needs no separate change — `CantinaRosterCsvWriter` iterates
  `roster.People` (line 66) from the same `WeeklyRosterDto`, so arrival-day humans flow through
  automatically. Extend the existing `tests/Humans.Web.Tests/Cantina/CantinaCsvWritersTests.cs` with
  a case asserting an arrival-day human (built into a `WeeklyRosterDto`) appears as a CSV row with
  `ArrivesOn` = their arrival day.
- [ ] **Step 2: Full build + test.**
  Run: `dotnet build Humans.slnx -v quiet && dotnet test Humans.slnx -v quiet`
  Expected: 0 errors, all tests PASS.
- [ ] **Step 3: Update the Cantina section invariant doc** (`docs/sections/Cantina.md`). The
  Pending/Confirmed "on-site" definition is repeated in **multiple places (≈ lines 20, 28, 56 plus the
  cross-section dependency note)** — update ALL of them to **Confirmed** or the doc self-contradicts.
  Also add the new arrival-day rule (fed on `firstConfirmedDay − 1`). Keep it terse, per the section
  template. Note: the `ArrivesOn` non-nullable invariant (≈ line 60) still holds — arrival-day humans
  get the arrival date as a real on-site day, so `daysList[0]` is valid.
- [ ] **Step 4: Commit docs.**
  ```bash
  git add docs/sections/Cantina.md
  git commit -m "docs(cantina): confirmed-only on-site + arrival-day feeding rule"
  ```

### Task 2.6: Open PR #2
- [ ] Push branch to `origin`, open PR to `origin/main` titled
  `feat(cantina): feed humans the day before their first confirmed shift`. Body: link the spec;
  **call out the two behavior changes for Peter's sign-off** — (1) cantina on-site is now
  confirmed-only (pending-only humans drop off), (2) `GetOnSiteUserIdsForDayAsync` semantics changed.
  Also note: **no new interface/repo/service surface was added** (reuse-first satisfied, no approval
  gate), and the per-render DB round-trips rise to ~40-60 sequential `GetOnSiteUserIdsForDayAsync`
  calls — a conscious trade documented in the spec (Peter will likely ask).

---

## Manual verification (after both PRs, per CLAUDE.md "exercise the change")
- Launch the app (`dotnet run --project src/Humans.Web`, dev login) and open the Cantina roster:
  - Confirm a human whose first confirmed shift is e.g. Wednesday now shows on Tuesday (no shift).
  - Confirm a pending-only human is absent.
  - Confirm the CSV export includes the arrival-day row.
  - Confirm the FODMAP checkbox is gone from Profile → Dietary/Medical and the intolerance rollup.
