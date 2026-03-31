# Shift Preference Wizard Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the flat `/Profile/ShiftInfo` form with a 3-step wizard (Skills, Work Style, Languages) using emoji chips, radio cards, and toggle switches.

**Architecture:** Server-rendered Razor view with vanilla JS step navigation. Single `<form>` wraps all 3 steps; POST on final step saves to existing `VolunteerEventProfile` via `ProfileController`. View model splits time preferences from toggle quirks for mutual exclusivity handling.

**Tech Stack:** ASP.NET Core MVC, Razor views, Bootstrap 5, vanilla JS, xUnit + AwesomeAssertions for tests, Playwright for e2e.

**Spec:** `docs/features/33-shift-preference-wizard.md`

---

## File Map

| File | Action | Responsibility |
|------|--------|----------------|
| `src/Humans.Web/Models/ShiftViewModels.cs` | Modify | Add `TimePreference`, `TimePreferenceOptions`, `ToggleQuirkOptions`; remove dietary/medical properties |
| `src/Humans.Web/Controllers/ProfileController.cs` | Modify | GET: split quirks into time pref + toggles; POST: merge them back, stop writing dietary fields |
| `src/Humans.Web/Views/Profile/ShiftInfo.cshtml` | Rewrite | 3-step wizard with chips, radio cards, toggles |
| `tests/Humans.Application.Tests/ViewModels/ShiftInfoViewModelTests.cs` | Create | Unit tests for time preference split/merge logic |
| `tests/e2e/tests/shift-preferences.spec.ts` | Create | E2e smoke tests for wizard page load, step navigation, save |

---

### Task 1: Update ShiftInfoViewModel

**Files:**
- Modify: `src/Humans.Web/Models/ShiftViewModels.cs:228-251`
- Create: `tests/Humans.Application.Tests/ViewModels/ShiftInfoViewModelTests.cs`

- [ ] **Step 1: Write tests for time preference extraction and merging**

Create `tests/Humans.Application.Tests/ViewModels/ShiftInfoViewModelTests.cs`:

```csharp
using AwesomeAssertions;
using Humans.Web.Models;
using Xunit;

namespace Humans.Application.Tests.ViewModels;

public class ShiftInfoViewModelTests
{
    [Fact]
    public void TimePreferenceOptions_contains_all_four_values()
    {
        ShiftInfoViewModel.TimePreferenceOptions.Should()
            .BeEquivalentTo(["Early Bird", "Night Owl", "All Day", "No Preference"]);
    }

    [Fact]
    public void ToggleQuirkOptions_excludes_time_preferences()
    {
        ShiftInfoViewModel.ToggleQuirkOptions.Should()
            .BeEquivalentTo(["Sober Shift", "Work In Shade", "Quiet Work", "Physical Work OK", "No Heights"]);

        // No overlap with time preferences
        ShiftInfoViewModel.ToggleQuirkOptions.Should()
            .NotContain(ShiftInfoViewModel.TimePreferenceOptions);
    }

    [Fact]
    public void ExtractTimePreference_returns_matching_value_from_quirks()
    {
        var quirks = new List<string> { "Sober Shift", "Night Owl", "No Heights" };

        var result = ShiftInfoViewModel.ExtractTimePreference(quirks);

        result.Should().Be("Night Owl");
    }

    [Fact]
    public void ExtractTimePreference_returns_null_when_no_time_pref()
    {
        var quirks = new List<string> { "Sober Shift", "No Heights" };

        var result = ShiftInfoViewModel.ExtractTimePreference(quirks);

        result.Should().BeNull();
    }

    [Fact]
    public void ExtractToggleQuirks_excludes_time_preferences()
    {
        var quirks = new List<string> { "Sober Shift", "Night Owl", "No Heights" };

        var result = ShiftInfoViewModel.ExtractToggleQuirks(quirks);

        result.Should().BeEquivalentTo(["Sober Shift", "No Heights"]);
    }

    [Fact]
    public void MergeQuirks_combines_time_pref_and_toggles()
    {
        var toggles = new List<string> { "Sober Shift", "No Heights" };

        var result = ShiftInfoViewModel.MergeQuirks("Early Bird", toggles);

        result.Should().BeEquivalentTo(["Sober Shift", "No Heights", "Early Bird"]);
    }

    [Fact]
    public void MergeQuirks_with_null_time_pref_returns_toggles_only()
    {
        var toggles = new List<string> { "Sober Shift" };

        var result = ShiftInfoViewModel.MergeQuirks(null, toggles);

        result.Should().BeEquivalentTo(["Sober Shift"]);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Humans.Application.Tests --filter "FullyQualifiedName~ShiftInfoViewModelTests" -v minimal`
Expected: Build errors — `TimePreferenceOptions`, `ToggleQuirkOptions`, `ExtractTimePreference`, `ExtractToggleQuirks`, `MergeQuirks` don't exist yet.

- [ ] **Step 3: Update ShiftInfoViewModel**

In `src/Humans.Web/Models/ShiftViewModels.cs`, replace the `ShiftInfoViewModel` class (lines 228–251) with:

```csharp
public class ShiftInfoViewModel
{
    public List<string> SelectedSkills { get; set; } = [];
    public List<string> SelectedQuirks { get; set; } = []; // Toggle quirks only (no time prefs)
    public string? TimePreference { get; set; } // Mutually exclusive: Early Bird, Night Owl, All Day, No Preference
    public List<string> SelectedLanguages { get; set; } = [];

    // Skill options with emoji prefixes for display
    public static readonly string[] SkillOptions = ["Bartending", "First Aid", "Driving", "Sound", "Electrical", "Construction", "Cooking", "Art", "DJ", "Other"];
    public static readonly string[] LanguageOptions = ["English", "Spanish", "German", "French", "Italian", "Portuguese", "Other"];

    // Time preferences — mutually exclusive, stored as quirk value
    public static readonly string[] TimePreferenceOptions = ["Early Bird", "Night Owl", "All Day", "No Preference"];

    // Toggle quirks — multi-select, separate from time preference
    public static readonly string[] ToggleQuirkOptions = ["Sober Shift", "Work In Shade", "Quiet Work", "Physical Work OK", "No Heights"];

    // Combined QuirkOptions kept for backward compatibility (downstream consumers read quirks as flat array)
    public static readonly string[] QuirkOptions = [.. ToggleQuirkOptions, .. TimePreferenceOptions];

    // Emoji maps for view rendering
    public static readonly Dictionary<string, string> SkillEmoji = new()
    {
        ["Bartending"] = "\U0001f378", ["Cooking"] = "\U0001f373", ["Sound"] = "\U0001f39a\ufe0f",
        ["DJ"] = "\U0001f3a7", ["First Aid"] = "\U0001fa7a", ["Electrical"] = "\u26a1",
        ["Driving"] = "\U0001f697", ["Construction"] = "\U0001f528", ["Art"] = "\U0001f3a8",
        ["Other"] = "\u2728"
    };

    public static readonly Dictionary<string, string> LanguageEmoji = new()
    {
        ["English"] = "\U0001f1ec\U0001f1e7", ["Spanish"] = "\U0001f1ea\U0001f1f8",
        ["French"] = "\U0001f1eb\U0001f1f7", ["German"] = "\U0001f1e9\U0001f1ea",
        ["Italian"] = "\U0001f1ee\U0001f1f9", ["Portuguese"] = "\U0001f1f5\U0001f1f9",
        ["Other"] = "\U0001f30d"
    };

    public static readonly Dictionary<string, string> TimePreferenceEmoji = new()
    {
        ["Early Bird"] = "\U0001f305", ["Night Owl"] = "\U0001f319",
        ["All Day"] = "\u2600\ufe0f", ["No Preference"] = "\U0001f937"
    };

    public static readonly Dictionary<string, string> TimePreferenceDesc = new()
    {
        ["Early Bird"] = "Morning shifts, set up and prep",
        ["Night Owl"] = "Evening and late-night shifts",
        ["All Day"] = "Flexible, morning through evening",
        ["No Preference"] = "I'll take whatever's needed"
    };

    /// <summary>Extract the time preference value from a flat quirks array.</summary>
    public static string? ExtractTimePreference(List<string> quirks)
        => quirks.FirstOrDefault(q => TimePreferenceOptions.Contains(q, StringComparer.Ordinal));

    /// <summary>Extract toggle quirks (excluding time preferences) from a flat quirks array.</summary>
    public static List<string> ExtractToggleQuirks(List<string> quirks)
        => quirks.Where(q => !TimePreferenceOptions.Contains(q, StringComparer.Ordinal)).ToList();

    /// <summary>Merge a time preference and toggle quirks back into a flat quirks array.</summary>
    public static List<string> MergeQuirks(string? timePreference, List<string> toggleQuirks)
    {
        var result = new List<string>(toggleQuirks ?? []);
        if (!string.IsNullOrEmpty(timePreference))
            result.Add(timePreference);
        return result;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Humans.Application.Tests --filter "FullyQualifiedName~ShiftInfoViewModelTests" -v minimal`
Expected: All 7 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Web/Models/ShiftViewModels.cs tests/Humans.Application.Tests/ViewModels/ShiftInfoViewModelTests.cs
git commit -m "feat(33): update ShiftInfoViewModel with time preference split and emoji maps"
```

---

### Task 2: Update ProfileController

**Files:**
- Modify: `src/Humans.Web/Controllers/ProfileController.cs:798-865`

- [ ] **Step 1: Update GET action**

Replace the `ShiftInfo()` GET action (lines 798–830) with:

```csharp
[HttpGet("/Profile/ShiftInfo")]
public async Task<IActionResult> ShiftInfo()
{
    try
    {
        var user = await GetCurrentUserAsync();
        if (user is null)
            return NotFound();

        var profile = await _profileService.GetShiftProfileAsync(user.Id, includeMedical: false);
        var allQuirks = profile?.Quirks ?? [];

        var viewModel = new ShiftInfoViewModel
        {
            SelectedSkills = profile?.Skills ?? [],
            SelectedQuirks = ShiftInfoViewModel.ExtractToggleQuirks(allQuirks),
            TimePreference = ShiftInfoViewModel.ExtractTimePreference(allQuirks),
            SelectedLanguages = profile?.Languages ?? [],
        };

        return View(viewModel);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Failed to load shift info for user");
        SetError("Failed to load shift info.");
        return RedirectToAction(nameof(Index));
    }
}
```

Key changes: `includeMedical: false`, split quirks into `SelectedQuirks` + `TimePreference`, removed dietary/allergy/medical fields.

- [ ] **Step 2: Update POST action**

Replace the `ShiftInfo(ShiftInfoViewModel model)` POST action (lines 832–865) with:

```csharp
[HttpPost("/Profile/ShiftInfo")]
[ValidateAntiForgeryToken]
public async Task<IActionResult> ShiftInfo(ShiftInfoViewModel model)
{
    try
    {
        var user = await GetCurrentUserAsync();
        if (user is null)
            return NotFound();

        var shiftProfile = await _profileService.GetOrCreateShiftProfileAsync(user.Id);

        shiftProfile.Skills = model.SelectedSkills ?? [];
        shiftProfile.Quirks = ShiftInfoViewModel.MergeQuirks(model.TimePreference, model.SelectedQuirks ?? []);
        shiftProfile.Languages = model.SelectedLanguages ?? [];

        await _profileService.UpdateShiftProfileAsync(shiftProfile);

        SetSuccess(_localizer["Profile_Updated"].Value);
        return RedirectToAction(nameof(ShiftInfo));
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Failed to save shift info for user");
        SetError("Failed to save shift info.");
        return View(model);
    }
}
```

Key changes: uses `MergeQuirks` to combine time pref + toggle quirks; stops writing dietary/allergy/medical fields. Existing dietary data on the entity is untouched.

- [ ] **Step 3: Build to verify compilation**

Run: `dotnet build src/Humans.Web`
Expected: Build will FAIL because the Razor view still references removed model properties (`SelectedAllergies`, `DietaryPreference`, etc.). This is expected — the view is rewritten in Task 3. Verify the error messages are only from `ShiftInfo.cshtml`, not from the controller or model.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/Controllers/ProfileController.cs
git commit -m "feat(33): update ProfileController to split time preference from toggle quirks"
```

---

### Task 3: Rewrite ShiftInfo.cshtml as Wizard

**Files:**
- Rewrite: `src/Humans.Web/Views/Profile/ShiftInfo.cshtml`

This is the largest task — the full wizard view. It replaces the entire existing file.

- [ ] **Step 1: Write the wizard view**

Rewrite `src/Humans.Web/Views/Profile/ShiftInfo.cshtml` with the complete wizard. The view has these sections:

1. **Header**: breadcrumb, step label, title, subtitle, progress dots
2. **Form**: single `<form>` wrapping all 3 step panes
3. **Step 1 (Skills)**: emoji chip grid with hidden checkboxes
4. **Step 2 (Work Style)**: radio cards for time preference + Bootstrap toggle switches for quirks
5. **Step 3 (Languages)**: emoji chip grid with hidden checkboxes
6. **Footer**: Back / Continue / Save buttons
7. **Style block**: chip, radio-card, progress-dot CSS
8. **Script block**: step navigation, chip toggle, radio card selection

```cshtml
@model Humans.Web.Models.ShiftInfoViewModel
@{
    ViewData["Title"] = "Shift Preferences";
}

<div class="row">
    <div class="col-12" style="max-width: 560px; margin: 0 auto;">
        <nav aria-label="breadcrumb">
            <ol class="breadcrumb">
                <li class="breadcrumb-item"><a asp-action="Index">Profile</a></li>
                <li class="breadcrumb-item active" aria-current="page">Shift Preferences</li>
            </ol>
        </nav>

        <vc:temp-data-alerts />

        @* Wizard header *@
        <div class="text-center mb-4">
            <div class="text-muted text-uppercase small" id="stepLabel">Step 1 of 3</div>
            <h1 class="h4 mt-1 mb-1" id="stepTitle">What are you good at?</h1>
            <p class="text-muted small" id="stepSubtitle">Select skills you'd like to use. You can change these anytime.</p>
            <div class="d-flex justify-content-center gap-2 mt-3" id="progressDots">
                <span class="progress-dot active" data-step="0"></span>
                <span class="progress-dot" data-step="1"></span>
                <span class="progress-dot" data-step="2"></span>
            </div>
        </div>

        <form asp-action="ShiftInfo" method="post" id="wizardForm">
            @Html.AntiForgeryToken()

            @* ===== STEP 0: Skills ===== *@
            <div class="step-pane active" id="step-0">
                <div class="chip-grid">
                    @foreach (var skill in Humans.Web.Models.ShiftInfoViewModel.SkillOptions)
                    {
                        var isSelected = Model.SelectedSkills.Contains(skill);
                        var emoji = Humans.Web.Models.ShiftInfoViewModel.SkillEmoji.GetValueOrDefault(skill, "");
                        <label class="chip @(isSelected ? "selected" : "")" role="checkbox" aria-checked="@(isSelected ? "true" : "false")" tabindex="0">
                            <input type="checkbox" name="SelectedSkills" value="@skill" class="d-none"
                                   @(isSelected ? "checked" : "") />
                            <span class="chip-emoji">@emoji</span> @skill
                        </label>
                    }
                </div>
            </div>

            @* ===== STEP 1: Work Style ===== *@
            <div class="step-pane" id="step-1">
                <p class="fw-bold small text-muted mb-2">When do you prefer to work?</p>
                <div class="radio-card-grid">
                    @foreach (var tp in Humans.Web.Models.ShiftInfoViewModel.TimePreferenceOptions)
                    {
                        var isSelected = string.Equals(Model.TimePreference, tp, StringComparison.Ordinal);
                        var emoji = Humans.Web.Models.ShiftInfoViewModel.TimePreferenceEmoji.GetValueOrDefault(tp, "");
                        var desc = Humans.Web.Models.ShiftInfoViewModel.TimePreferenceDesc.GetValueOrDefault(tp, "");
                        <label class="radio-card @(isSelected ? "selected" : "")" tabindex="0">
                            <input type="radio" name="TimePreference" value="@tp" class="d-none"
                                   @(isSelected ? "checked" : "") />
                            <div class="rc-emoji">@emoji</div>
                            <div class="rc-label">@tp</div>
                            <div class="rc-desc">@desc</div>
                        </label>
                    }
                </div>

                <p class="fw-bold small text-muted mb-2 mt-4">Other preferences</p>
                @foreach (var quirk in Humans.Web.Models.ShiftInfoViewModel.ToggleQuirkOptions)
                {
                    var isChecked = Model.SelectedQuirks.Contains(quirk);
                    <div class="form-check form-switch mb-2">
                        <input type="checkbox" name="SelectedQuirks" value="@quirk" class="form-check-input"
                               id="quirk-@quirk.Replace(" ", "-")" @(isChecked ? "checked" : "") />
                        <label class="form-check-label" for="quirk-@quirk.Replace(" ", "-")">@quirk</label>
                    </div>
                }
            </div>

            @* ===== STEP 2: Languages ===== *@
            <div class="step-pane" id="step-2">
                <div class="chip-grid">
                    @foreach (var lang in Humans.Web.Models.ShiftInfoViewModel.LanguageOptions)
                    {
                        var isSelected = Model.SelectedLanguages.Contains(lang);
                        var emoji = Humans.Web.Models.ShiftInfoViewModel.LanguageEmoji.GetValueOrDefault(lang, "");
                        <label class="chip @(isSelected ? "selected" : "")" role="checkbox" aria-checked="@(isSelected ? "true" : "false")" tabindex="0">
                            <input type="checkbox" name="SelectedLanguages" value="@lang" class="d-none"
                                   @(isSelected ? "checked" : "") />
                            <span class="chip-emoji">@emoji</span> @lang
                        </label>
                    }
                </div>
            </div>

            @* ===== Navigation ===== *@
            <div class="d-flex justify-content-between align-items-center mt-4">
                <button type="button" class="btn btn-outline-secondary" id="backBtn" style="display:none" onclick="prevStep()">
                    &larr; Back
                </button>
                <div></div>
                <button type="button" class="btn btn-primary" id="nextBtn" onclick="nextStep()">
                    Continue &rarr;
                </button>
                <button type="submit" class="btn btn-primary" id="saveBtn" style="display:none">
                    Save
                </button>
            </div>
        </form>
    </div>
</div>

@section Styles {
<style>
    /* Progress dots */
    .progress-dot {
        width: 10px; height: 10px; border-radius: 50%;
        background: #dee2e6; display: inline-block; transition: background 0.2s;
    }
    .progress-dot.active, .progress-dot.done { background: #0d6efd; }

    /* Step panes */
    .step-pane { display: none; }
    .step-pane.active { display: block; }

    /* Chip grid */
    .chip-grid { display: flex; flex-wrap: wrap; gap: 10px; }
    .chip {
        display: inline-flex; align-items: center; gap: 4px;
        padding: 9px 16px; border-radius: 10px; cursor: pointer;
        background: #f8f9fa; border: 1.5px solid #dee2e6; color: #495057;
        font-size: 14px; user-select: none; transition: all 0.15s;
    }
    .chip:hover { border-color: #adb5bd; }
    .chip:focus-visible { outline: 2px solid #0d6efd; outline-offset: 2px; }
    .chip.selected {
        background: #e7f1ff; border-color: #0d6efd; color: #0d6efd; font-weight: 500;
    }
    .chip-emoji { font-size: 16px; }

    /* Radio cards */
    .radio-card-grid {
        display: grid; grid-template-columns: 1fr 1fr; gap: 10px;
    }
    @@media (max-width: 575.98px) {
        .radio-card-grid { grid-template-columns: 1fr; }
    }
    .radio-card {
        display: flex; flex-direction: column; align-items: center;
        padding: 16px 12px; border-radius: 10px; cursor: pointer; text-align: center;
        background: #f8f9fa; border: 1.5px solid #dee2e6; transition: all 0.15s;
    }
    .radio-card:hover { border-color: #adb5bd; }
    .radio-card:focus-visible { outline: 2px solid #0d6efd; outline-offset: 2px; }
    .radio-card.selected {
        background: #e7f1ff; border-color: #0d6efd;
    }
    .rc-emoji { font-size: 24px; margin-bottom: 6px; }
    .rc-label { font-weight: 600; font-size: 14px; color: #212529; }
    .rc-desc { font-size: 12px; color: #6c757d; margin-top: 2px; }
</style>
}

@section Scripts {
<script>
(function() {
    const STEPS = [
        { label: 'Step 1 of 3', title: 'What are you good at?', subtitle: 'Select skills you\'d like to use. You can change these anytime.' },
        { label: 'Step 2 of 3', title: 'How do you like to work?', subtitle: 'Help coordinators match you with shifts that fit your style and availability.' },
        { label: 'Step 3 of 3', title: 'Which languages do you speak?', subtitle: 'This helps us match you with teams where you can communicate well.' }
    ];

    let current = 0;
    const stepLabel = document.getElementById('stepLabel');
    const stepTitle = document.getElementById('stepTitle');
    const stepSubtitle = document.getElementById('stepSubtitle');
    const backBtn = document.getElementById('backBtn');
    const nextBtn = document.getElementById('nextBtn');
    const saveBtn = document.getElementById('saveBtn');
    const dots = document.querySelectorAll('.progress-dot');

    function setStep(n) {
        document.getElementById('step-' + current).classList.remove('active');
        current = n;
        document.getElementById('step-' + current).classList.add('active');

        stepLabel.textContent = STEPS[n].label;
        stepTitle.textContent = STEPS[n].title;
        stepSubtitle.textContent = STEPS[n].subtitle;

        dots.forEach(function(dot, i) {
            dot.classList.remove('active', 'done');
            if (i < n) dot.classList.add('done');
            if (i === n) dot.classList.add('active');
        });

        backBtn.style.display = n === 0 ? 'none' : '';
        nextBtn.style.display = n === STEPS.length - 1 ? 'none' : '';
        saveBtn.style.display = n === STEPS.length - 1 ? '' : 'none';
    }

    window.nextStep = function() { if (current < STEPS.length - 1) setStep(current + 1); };
    window.prevStep = function() { if (current > 0) setStep(current - 1); };

    // Chip toggle
    document.querySelectorAll('.chip').forEach(function(chip) {
        function toggle() {
            var cb = chip.querySelector('input[type="checkbox"]');
            cb.checked = !cb.checked;
            chip.classList.toggle('selected', cb.checked);
            chip.setAttribute('aria-checked', cb.checked ? 'true' : 'false');
        }
        chip.addEventListener('click', function(e) {
            if (e.target.tagName !== 'INPUT') toggle();
        });
        chip.addEventListener('keydown', function(e) {
            if (e.key === ' ' || e.key === 'Enter') { e.preventDefault(); toggle(); }
        });
    });

    // Radio card toggle
    document.querySelectorAll('.radio-card').forEach(function(card) {
        function select() {
            document.querySelectorAll('.radio-card').forEach(function(c) { c.classList.remove('selected'); });
            card.classList.add('selected');
            card.querySelector('input[type="radio"]').checked = true;
        }
        card.addEventListener('click', function(e) {
            if (e.target.tagName !== 'INPUT') select();
        });
        card.addEventListener('keydown', function(e) {
            if (e.key === ' ' || e.key === 'Enter') { e.preventDefault(); select(); }
        });
    });
})();
</script>
}
```

- [ ] **Step 2: Build to verify view compiles**

Run: `dotnet build src/Humans.Web`
Expected: Build succeeds with no errors.

- [ ] **Step 3: Run all existing tests to verify no regressions**

Run: `dotnet test Humans.slnx -v minimal`
Expected: All tests pass. No existing tests reference the removed dietary/medical properties on `ShiftInfoViewModel`.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/Views/Profile/ShiftInfo.cshtml
git commit -m "feat(33): rewrite ShiftInfo as 3-step wizard with chips, radio cards, toggles"
```

---

### Task 4: E2E Smoke Tests

**Files:**
- Create: `tests/e2e/tests/shift-preferences.spec.ts`

- [ ] **Step 1: Write e2e tests**

Create `tests/e2e/tests/shift-preferences.spec.ts`:

```typescript
import { test, expect } from '@playwright/test';
import { loginAsVolunteer } from '../helpers/auth';

test.describe('Shift Preference Wizard (33-shift-preference-wizard)', () => {
  test('page loads with wizard step 1', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/Profile/ShiftInfo');

    await expect(page.locator('#stepTitle')).toHaveText('What are you good at?');
    await expect(page.locator('#stepLabel')).toHaveText('Step 1 of 3');
    await expect(page.locator('.progress-dot.active')).toHaveCount(1);
  });

  test('can navigate through all 3 steps', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/Profile/ShiftInfo');

    // Step 1 -> Step 2
    await page.click('#nextBtn');
    await expect(page.locator('#stepTitle')).toHaveText('How do you like to work?');
    await expect(page.locator('#stepLabel')).toHaveText('Step 2 of 3');

    // Step 2 -> Step 3
    await page.click('#nextBtn');
    await expect(page.locator('#stepTitle')).toHaveText('Which languages do you speak?');
    await expect(page.locator('#stepLabel')).toHaveText('Step 3 of 3');
    await expect(page.locator('#saveBtn')).toBeVisible();
    await expect(page.locator('#nextBtn')).not.toBeVisible();
  });

  test('can go back from step 3 to step 1', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/Profile/ShiftInfo');

    await page.click('#nextBtn');
    await page.click('#nextBtn');
    await page.click('#backBtn');
    await expect(page.locator('#stepTitle')).toHaveText('How do you like to work?');

    await page.click('#backBtn');
    await expect(page.locator('#stepTitle')).toHaveText('What are you good at?');
    await expect(page.locator('#backBtn')).not.toBeVisible();
  });

  test('selecting a chip toggles it', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/Profile/ShiftInfo');

    const chip = page.locator('.chip', { hasText: 'Bartending' });
    await chip.click();
    await expect(chip).toHaveClass(/selected/);

    await chip.click();
    await expect(chip).not.toHaveClass(/selected/);
  });

  test('save submits and redirects back', async ({ page }) => {
    await loginAsVolunteer(page);
    await page.goto('/Profile/ShiftInfo');

    // Select a skill
    await page.locator('.chip', { hasText: 'Sound' }).click();

    // Navigate to step 3
    await page.click('#nextBtn');
    await page.click('#nextBtn');

    // Save
    await page.click('#saveBtn');

    // Should redirect back to same page with success message
    await expect(page).toHaveURL(/\/Profile\/ShiftInfo/);
  });
});
```

- [ ] **Step 2: Commit**

```bash
git add tests/e2e/tests/shift-preferences.spec.ts
git commit -m "test(33): add e2e smoke tests for shift preference wizard"
```

---

### Task 5: Manual Verification & Cleanup

- [ ] **Step 1: Run full test suite**

Run: `dotnet test Humans.slnx -v minimal`
Expected: All tests pass.

- [ ] **Step 2: Run the app locally and verify the wizard**

Run: `dotnet run --project src/Humans.Web`

Manual checks:
- Navigate to `/Profile/ShiftInfo`
- Verify step 1 shows skills as emoji chips
- Click chips to select/deselect, verify visual toggle
- Navigate to step 2, verify radio cards and toggle switches
- Navigate to step 3, verify language chips
- Save with selections, verify redirect with success message
- Reload page, verify selections are pre-populated
- Test on narrow viewport (mobile), verify chips wrap and radio cards stack

- [ ] **Step 3: Verify dietary data is preserved**

Check that an existing user's dietary/allergy/medical data is still in the database (not wiped by saving the wizard). The POST action no longer touches those fields, so they should remain untouched.

- [ ] **Step 4: Update feature doc post-fix check**

Review `docs/features/33-shift-preference-wizard.md` — confirm it matches what was implemented. No changes expected unless implementation deviated from spec.
