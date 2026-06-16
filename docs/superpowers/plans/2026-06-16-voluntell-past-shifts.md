# Voluntell for Past Shifts Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let coordinators voluntell/manage humans on shifts that have already ended (retroactive rota correction), without sending the human a now-meaningless "you were assigned" notification.

**Architecture:** The service already permits past assignment; the only true gap is the view hides the Voluntell control on past shifts. So this is (a) two small service guards that suppress the volunteer-facing `ShiftAssigned` notification for past shifts, and (b) a Razor change that surfaces Voluntell on past shifts while leaving the existing past-management panel as the source of Remove/No-Show/Bail. No domain, schema, or migration changes. No locale changes (admin-view i18n exemption).

**Tech Stack:** .NET / EF Core / NodaTime / Razor (MVC). Tests: xUnit (`[HumansFact]`), NSubstitute, AwesomeAssertions, real `ShiftRepository` over the test DB via `ServiceTestHarness`.

**Spec:** `docs/superpowers/specs/2026-06-16-voluntell-past-shifts-design.md`

**Key facts the implementer needs:**
- Build with `dotnet build Humans.slnx -v quiet`; test with `dotnet test Humans.slnx -v quiet` (the `-v quiet` is mandatory — see `memory/process/dotnet-verbosity-quiet.md`).
- Run a single test project: `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj -v quiet --filter "FullyQualifiedName~ShiftSignupServiceTests"`.
- "Past" is decided by `shift.GetAbsoluteEnd(es) <= now` (`now = clock.GetCurrentInstant()`), the same call the view uses (`isPast = now > shiftEnd`).
- Test clock is fixed: `TestNow = 2026-06-15 12:00 UTC` (`ShiftSignupServiceTests` line 29). Event timezone is Madrid (UTC+2 in summer).
- `ServiceTestHarness` already exposes shared NSubstitute mocks `Notifier` (`INotificationEmitter`) and `AuditLog` (`IAuditLogService`). The Shifts test currently passes an *inline* `Substitute.For<INotificationEmitter>()` instead of `Notifier` — Task 1 fixes that so notifications can be asserted.
- `INotificationEmitter.SendAsync(NotificationSource source, NotificationClass, NotificationPriority, string title, IReadOnlyList<Guid> recipients, string? body=null, string? actionUrl=null, string? actionLabel=null, string? targetGroupName=null, string? sourceKey=null, CancellationToken=default)`.
- The volunteer-facing send uses `NotificationSource.ShiftAssigned`; the coordinator ping uses `NotificationSource.ShiftSignupChange`. Both flow through the same `Notifier`, so assertions must match on the `ShiftAssigned` source specifically.

---

## Chunk 1: Voluntell on past shifts

### Task 1: Suppress `ShiftAssigned` for a past single-shift voluntell

**Files:**
- Modify (test harness wiring): `tests/Humans.Application.Tests/Services/Shifts/ShiftSignupServiceTests.cs:60`
- Test: `tests/Humans.Application.Tests/Services/Shifts/ShiftSignupServiceTests.cs` (new tests in the `// Voluntell` region, after line ~302)
- Modify (implementation): `src/Humans.Application/Services/Shifts/ShiftSignupService.cs:303-317` (the `ShiftAssigned` try/catch in `VoluntellAsync`)

- [ ] **Step 1: Wire the harness notification mock so it can be asserted**

In `ShiftSignupServiceTests` constructor, change the 6th constructor argument from the inline substitute to the harness-provided `Notifier`:

```csharp
// line 60 — was: Substitute.For<INotificationEmitter>(),
Notifier,
```

Leave every other constructor argument unchanged.

- [ ] **Step 2: Write the failing tests**

Add to `ShiftSignupServiceTests.cs` in the Voluntell region (after `Voluntell_CreatesConfirmedWithEnrolledFlag`, ~line 302). No new `using` directives are needed: `INotificationEmitter`/`NotificationSource` come from `Humans.Domain.Enums` (`NotificationSource`, line 10) and `Humans.Application.Interfaces.Notifications` (`INotificationEmitter`, line 13), `AuditAction` from `Humans.Domain.Enums` (line 10), and `CancellationToken`/`IReadOnlyList<>` resolve via implicit usings (other tests use `Arg.Any<CancellationToken>()` with no import).

```csharp
[HumansFact]
public async Task Voluntell_FutureShift_SendsAssignedNotification()
{
    // Default scenario seeds shifts around gate opening 2026-07-01 → future vs TestNow (2026-06-15).
    var (_, _, shift) = SeedShiftScenario(SignupPolicy.RequireApproval);
    var volunteerId = Guid.NewGuid();
    var enrollerId = Guid.NewGuid();
    await Db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

    var result = await _service.VoluntellAsync(volunteerId, shift.Id, enrollerId);

    result.Success.Should().BeTrue();
    await Notifier.Received(1).SendAsync(
        NotificationSource.ShiftAssigned,
        Arg.Any<NotificationClass>(), Arg.Any<NotificationPriority>(),
        Arg.Any<string>(), Arg.Any<IReadOnlyList<Guid>>(),
        Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
        Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
}

[HumansFact]
public async Task Voluntell_PastShift_SuppressesAssignedNotification_ButStillAuditsAndConfirms()
{
    // Make the shift end in the past, mirroring MarkNoShow_AfterShiftEnd:
    // gate opening 2026-06-14, day 0, 08:00 +2h → ends 10:00 Madrid = 08:00 UTC < TestNow (12:00 UTC).
    var (es, _, shift) = SeedShiftScenario(SignupPolicy.RequireApproval);
    es.GateOpeningDate = new LocalDate(2026, 6, 14);
    shift.DayOffset = 0;
    shift.StartTime = new LocalTime(8, 0);
    shift.Duration = Duration.FromHours(2);
    var volunteerId = Guid.NewGuid();
    var enrollerId = Guid.NewGuid();
    await Db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

    var result = await _service.VoluntellAsync(volunteerId, shift.Id, enrollerId);

    result.Success.Should().BeTrue();
    result.Signup!.Status.Should().Be(SignupStatus.Confirmed);

    // Volunteer-facing ShiftAssigned must NOT be sent for a past shift...
    await Notifier.DidNotReceive().SendAsync(
        NotificationSource.ShiftAssigned,
        Arg.Any<NotificationClass>(), Arg.Any<NotificationPriority>(),
        Arg.Any<string>(), Arg.Any<IReadOnlyList<Guid>>(),
        Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
        Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());

    // ...but the audit entry is still written.
    await AuditLog.Received().LogAsync(
        AuditAction.ShiftSignupVoluntold, Arg.Any<string>(), Arg.Any<Guid>(),
        Arg.Any<string>(), enrollerId, volunteerId, Arg.Any<string>());
}
```

> Note on the `AuditLog.LogAsync` matcher: it matches the real `IAuditLogService.LogAsync` overload used at `ShiftSignupService.cs:294-298` (7 args). Passing `enrollerId`/`volunteerId` (`Guid`) into the `Guid?` parameters is the expected, correct form (implicit `Guid → Guid?`); mirror the existing passing matchers at test file lines ~849-853. Only adjust if the real signature actually differs — never change production code to fit the test.

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj -v quiet --filter "FullyQualifiedName~Voluntell_PastShift_SuppressesAssignedNotification|FullyQualifiedName~Voluntell_FutureShift_SendsAssignedNotification"`
Expected: `Voluntell_PastShift_...` FAILS (ShiftAssigned currently sent regardless of date). `Voluntell_FutureShift_...` should PASS already (guards the no-regression direction).

- [ ] **Step 4: Implement the guard**

In `src/Humans.Application/Services/Shifts/ShiftSignupService.cs`, `VoluntellAsync`, wrap the existing `ShiftAssigned` try/catch (lines 303-317) in a past-shift guard. `now` (line 262) and `es` (line 261) are already in scope:

```csharp
if (shift.GetAbsoluteEnd(es) > now)
{
    try
    {
        await notificationService.SendAsync(
            NotificationSource.ShiftAssigned,
            NotificationClass.Informational,
            NotificationPriority.Normal,
            $"You were assigned to {shift.Rota.Name} on day {shift.DayOffset}",
            [userId],
            actionUrl: "/Shifts",
            actionLabel: "View shifts");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to dispatch ShiftAssigned notification for user {UserId} shift {ShiftId}", userId, shiftId);
    }
}
```

Leave the audit (line 294) and `DispatchSignupChangeNotificationAsync` (line 300) calls before it untouched.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj -v quiet --filter "FullyQualifiedName~ShiftSignupServiceTests"`
Expected: PASS (new tests + all pre-existing Voluntell tests).

- [ ] **Step 6: Commit**

```bash
git add src/Humans.Application/Services/Shifts/ShiftSignupService.cs tests/Humans.Application.Tests/Services/Shifts/ShiftSignupServiceTests.cs
git commit -m "feat(shifts): suppress assigned-notification for past single-shift voluntell"
```

---

### Task 2: Suppress the aggregate `ShiftAssigned` for an all-past range voluntell

**Files:**
- Test: `tests/Humans.Application.Tests/Services/Shifts/ShiftSignupServiceTests.cs` (new tests in the `// VoluntellRange` region, after ~line 830)
- Modify (implementation): `src/Humans.Application/Services/Shifts/ShiftSignupService.cs:427-441` (the aggregate `ShiftAssigned` try/catch in `VoluntellRangeAsync`)

- [ ] **Step 1: Write the failing tests**

```csharp
[HumansFact]
public async Task VoluntellRange_AllPast_SuppressesAssignedNotification()
{
    // gate opening 2026-06-14 → build days -3..-1 = 2026-06-11..13, all-day windows end
    // 18:00 Madrid = 16:00 UTC, all before TestNow (2026-06-15 12:00 UTC) → all past.
    var (es, rota, _) = SeedShiftScenario(SignupPolicy.Public);
    es.GateOpeningDate = new LocalDate(2026, 6, 14);
    rota.Period = RotaPeriod.Build;
    for (var day = -3; day <= -1; day++)
        SeedAllDayShift(rota, day);
    var volunteerId = Guid.NewGuid();
    var enrollerId = Guid.NewGuid();
    await Db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

    var result = await _service.VoluntellRangeAsync(volunteerId, rota.Id, -3, -1, enrollerId);

    result.Success.Should().BeTrue();
    await Notifier.DidNotReceive().SendAsync(
        NotificationSource.ShiftAssigned,
        Arg.Any<NotificationClass>(), Arg.Any<NotificationPriority>(),
        Arg.Any<string>(), Arg.Any<IReadOnlyList<Guid>>(),
        Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
        Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
}

[HumansFact]
public async Task VoluntellRange_SpansPastAndFuture_SendsAssignedNotification()
{
    // gate opening 2026-06-16 → days -3..-1 = 2026-06-13,14,15. Day -1 (2026-06-15) all-day
    // window ends 18:00 Madrid = 16:00 UTC > TestNow (12:00 UTC) → future present in range.
    var (es, rota, _) = SeedShiftScenario(SignupPolicy.Public);
    es.GateOpeningDate = new LocalDate(2026, 6, 16);
    rota.Period = RotaPeriod.Build;
    for (var day = -3; day <= -1; day++)
        SeedAllDayShift(rota, day);
    var volunteerId = Guid.NewGuid();
    var enrollerId = Guid.NewGuid();
    await Db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

    var result = await _service.VoluntellRangeAsync(volunteerId, rota.Id, -3, -1, enrollerId);

    result.Success.Should().BeTrue();
    await Notifier.Received(1).SendAsync(
        NotificationSource.ShiftAssigned,
        Arg.Any<NotificationClass>(), Arg.Any<NotificationPriority>(),
        Arg.Any<string>(), Arg.Any<IReadOnlyList<Guid>>(),
        Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
        Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
}
```

> The `rota.Period = RotaPeriod.Build;` + `SeedAllDayShift(rota, day)` lines are **required**: `SeedShiftScenario` only seeds a single *timed* shift at day +1, but `VoluntellRangeAsync` → `SelectAllDayRangeShifts` filters strictly on `IsAllDay && DayOffset in [start,end]`. Without seeding all-day shifts at -3..-1 the range finds nothing and returns `Fail("No shifts found…")`. This mirrors the existing passing `VoluntellRange_CreatesConfirmedSignupsAcrossDateRange` (line ~765).

- [ ] **Step 2: Run the tests to verify the all-past one fails**

Run: `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj -v quiet --filter "FullyQualifiedName~VoluntellRange_AllPast|FullyQualifiedName~VoluntellRange_SpansPastAndFuture"`
Expected: `VoluntellRange_AllPast_...` FAILS (aggregate sent regardless of date). `VoluntellRange_SpansPastAndFuture_...` PASSES already.

- [ ] **Step 3: Implement the guard**

In `VoluntellRangeAsync`, wrap the aggregate `ShiftAssigned` try/catch (lines 427-441) so it only fires when at least one assigned shift ends in the future. `now` (line 376), `es` (line 342), and `assignable` (line 373) are all in scope:

```csharp
if (assignable.Any(s => s.GetAbsoluteEnd(es) > now))
{
    try
    {
        await notificationService.SendAsync(
            NotificationSource.ShiftAssigned,
            NotificationClass.Informational,
            NotificationPriority.Normal,
            $"You were assigned to {rota.Name} ({assignable.Count} shifts)",
            [userId],
            actionUrl: "/Shifts",
            actionLabel: "View shifts");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to dispatch ShiftAssigned notification for user {UserId} rota {RotaId}", userId, rotaId);
    }
}
```

Leave the audit loop (lines 415-422) and `DispatchSignupChangeNotificationAsync` (line 424) untouched.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj -v quiet --filter "FullyQualifiedName~ShiftSignupServiceTests"`
Expected: PASS (new range tests + all pre-existing).

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Application/Services/Shifts/ShiftSignupService.cs tests/Humans.Application.Tests/Services/Shifts/ShiftSignupServiceTests.cs
git commit -m "feat(shifts): suppress assigned-notification for all-past range voluntell"
```

---

### Task 3: Surface Voluntell on past shifts in the department admin view

No unit test — Razor view gating is verified manually (per spec: view-template gating is throwaway-grade).

**Files:**
- Modify: `src/Humans.Web/Views/ShiftAdmin/Index.cshtml` (three edits: lines ~416, ~469-487, ~662)

- [ ] **Step 1: Split the actions block so Voluntell is not gated by `isPast`**

At `src/Humans.Web/Views/ShiftAdmin/Index.cshtml` lines 469-487, the `@if (Model.CanApproveSignups && !isPast)` block wraps both the Manage button and the Voluntell button. Keep Manage inside the `!isPast` block; move the Voluntell button into its own `@if (Model.CanApproveSignups)` block immediately after.

Replace:

```razor
                                            @if (Model.CanApproveSignups && !isPast)
                                            {
                                                var confirmedSignups = shift.ShiftSignups
                                                    .Where(ss => ss.Status == SignupStatus.Confirmed)
                                                    .ToList();
                                                if (confirmedSignups.Count > 0)
                                                {
                                                    var manageBtnId = "manage-" + shift.Id.ToString("N");
                                                    <button class="btn btn-sm btn-outline-secondary ms-1" type="button"
                                                            data-bs-toggle="collapse" data-bs-target="#@manageBtnId">
                                                        Manage (@confirmedSignups.Count)
                                                    </button>
                                                }
                                                var voluntellBtnId = "dept-voluntell-" + shift.Id.ToString("N");
                                                <button class="btn btn-sm btn-outline-success ms-1" type="button"
                                                        data-bs-toggle="collapse" data-bs-target="#@voluntellBtnId">
                                                    Voluntell
                                                </button>
                                            }
```

With:

```razor
                                            @if (Model.CanApproveSignups && !isPast)
                                            {
                                                var confirmedSignups = shift.ShiftSignups
                                                    .Where(ss => ss.Status == SignupStatus.Confirmed)
                                                    .ToList();
                                                if (confirmedSignups.Count > 0)
                                                {
                                                    var manageBtnId = "manage-" + shift.Id.ToString("N");
                                                    <button class="btn btn-sm btn-outline-secondary ms-1" type="button"
                                                            data-bs-toggle="collapse" data-bs-target="#@manageBtnId">
                                                        Manage (@confirmedSignups.Count)
                                                    </button>
                                                }
                                            }
                                            @if (Model.CanApproveSignups)
                                            {
                                                var voluntellBtnId = "dept-voluntell-" + shift.Id.ToString("N");
                                                <button class="btn btn-sm btn-outline-success ms-1" type="button"
                                                        data-bs-toggle="collapse" data-bs-target="#@voluntellBtnId">
                                                    Voluntell
                                                </button>
                                            }
```

- [ ] **Step 2: Relax the Voluntell collapse body gate**

At line ~662, change the Voluntell body gate from `@if (Model.CanApproveSignups && !isPast)` to `@if (Model.CanApproveSignups)`:

```razor
                                    @if (Model.CanApproveSignups)
                                    {
                                        var voluntellCollapseId = "dept-voluntell-" + shift.Id.ToString("N");
                                        <tr class="collapse" id="@voluntellCollapseId">
```

Do **not** touch the Manage collapse body at line ~567 (`@if (!isPast && Model.CanApproveSignups)`) or the past-signups panel at line ~601 (`@if (isPast && shiftSignups.Count > 0 && ...)`) — they already provide Remove/No-Show/Bail for past shifts.

- [ ] **Step 3: Add the muted "past" badge in the date cell**

At line 416 the date cell renders the phase badge. Add a hardcoded "past" badge (no `@Localizer` — admin-view i18n exemption) when `isPast` (in scope from line 382), placed so it does not overlap the `phaseBadge`:

Replace:

```razor
                                        <td><span class="badge @phaseBadge me-1">@phaseLabel</span>@shiftDateLabel</td>
```

With:

```razor
                                        <td><span class="badge @phaseBadge me-1">@phaseLabel</span>@if (isPast){<span class="badge bg-secondary bg-opacity-50 me-1">past</span>}@shiftDateLabel</td>
```

- [ ] **Step 4: Build and manually verify**

Run: `dotnet build Humans.slnx -v quiet`
Expected: 0 errors.

Manual check (record what you observe): run the app (`dotnet run --project src/Humans.Web`), open a department's shift admin page (`/Teams/{slug}/Shifts`) as a coordinator on a rota that has at least one already-ended shift. Confirm: (a) the past shift shows a muted "past" badge; (b) the **Voluntell** button is present on the past shift and its search/assign collapse opens; (c) the **Manage** button is absent on the past shift while the existing "Signups (N)" panel still offers Remove / Mark No-Show. If you cannot run the app in this environment, state that explicitly and leave the manual check unchecked for the human.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Web/Views/ShiftAdmin/Index.cshtml
git commit -m "feat(shifts): show Voluntell + past badge on ended shifts in dept admin"
```

---

### Task 4: Update the Shifts section invariant doc

**Files:**
- Modify: `docs/sections/Shifts.md` (the invariants region, ~line 269)

- [ ] **Step 1: Read the relevant invariant + trigger regions**

Open `docs/sections/Shifts.md` around line 269 (voluntelling / early-entry-freeze invariants), the NoShow invariant (~line 227), and the Voluntelling **trigger** line (~line 272) — which currently states `ShiftAssigned` is fired to the volunteer on every voluntell. That trigger becomes false for past shifts and must be amended in Step 2.

- [ ] **Step 2: Add/adjust the invariant**

Add a concise invariant capturing both behaviors, in the doc's existing terse style. Cover:
- Coordinators (`CanApproveSignups`) may voluntell humans onto shifts that have already ended (retroactive rota correction); self-signup remains unavailable for past shifts.
- The volunteer-facing `ShiftAssigned` notification is suppressed when the shift has ended (single-shift: shift end ≤ now; range: suppressed only when *all* assigned shifts are past). Audit (`ShiftSignupVoluntold`) and the coordinator `ShiftSignupChange` ping are always emitted.
- Past-shift management (Remove / Mark No-Show / Bail Range) is served by the existing past-signups panel, not the future-only Manage control.

Also amend the stale Voluntelling trigger line (~line 269) so it reflects the suppression: `ShiftAssigned` fires only when the shift (single) or at least one assigned shift (range) ends in the future.

- [ ] **Step 3: Commit**

```bash
git add docs/sections/Shifts.md
git commit -m "docs(shifts): record past-shift voluntell + notification-suppression invariant"
```

---

### Task 5: Full verification

- [ ] **Step 1: Build the solution**

Run: `dotnet build Humans.slnx -v quiet`
Expected: 0 errors (pre-existing analyzer warnings are fine).

- [ ] **Step 2: Run the Shifts test suite**

Run: `dotnet test tests/Humans.Application.Tests/Humans.Application.Tests.csproj -v quiet --filter "FullyQualifiedName~Shift"`
Expected: all PASS, including the 4 new tests (`Voluntell_FutureShift_...`, `Voluntell_PastShift_...`, `VoluntellRange_AllPast_...`, `VoluntellRange_SpansPastAndFuture_...`).

- [ ] **Step 3: Confirm clean tree**

Run: `git status --porcelain`
Expected: empty (all work committed across Tasks 1-4).
