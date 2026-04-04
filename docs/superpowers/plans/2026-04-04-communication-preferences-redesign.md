# Communication Preferences Redesign (#316)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Redesign the Notification Settings page into a granular Communication Preferences page with positive opt-in checkboxes, a table layout with Email/Alert columns, new message categories, and facilitated-message blocking.

**Architecture:** Expand the `MessageCategory` enum with 5 new values. Keep the existing `CommunicationPreference` entity unchanged — it already has `OptedOut` (email) and `InboxEnabled` (alert) which map to the two channel columns. Invert the `OptedOut` semantics in the view model (new `EmailEnabled` = `!OptedOut`). Write a data migration to split `EventOperations` into `VolunteerUpdates` + `TeamUpdates` and rename `CommunityUpdates` → `FacilitatedMessages`. The ticketing "per-year" locking is a runtime check against `TicketOrders`, not a data model change.

**Tech Stack:** ASP.NET Core MVC, EF Core (Npgsql), NodaTime, xUnit + InMemoryDatabase

**Key files being changed:**
- `src/Humans.Domain/Enums/MessageCategory.cs` — enum + extension methods
- `src/Humans.Application/NotificationSourceMapping.cs` — source → category map
- `src/Humans.Infrastructure/Services/CommunicationPreferenceService.cs` — defaults, locked categories
- `src/Humans.Application/Interfaces/ICommunicationPreferenceService.cs` — interface (minor)
- `src/Humans.Web/Models/CommunicationPreferenceViewModels.cs` — view model redesign
- `src/Humans.Web/Controllers/ProfileController.cs` — controller actions + helpers
- `src/Humans.Web/Views/Profile/Notifications.cshtml` → rename to `CommunicationPreferences.cshtml` — full view rewrite
- `src/Humans.Web/Views/Profile/Index.cshtml` — quick link text
- `src/Humans.Web/ViewComponents/ProfileCardViewComponent.cs` — facilitated message blocking
- `tests/Humans.Domain.Tests/Enums/EnumStringStabilityTests.cs` — add MessageCategory
- New EF migration for data migration

---

### Task 1: Expand MessageCategory Enum

**Files:**
- Modify: `src/Humans.Domain/Enums/MessageCategory.cs`

- [ ] **Step 1: Add new enum values and update extension methods**

```csharp
namespace Humans.Domain.Enums;

/// <summary>
/// Categories of system communications for preference management.
/// Stored as string in DB; new values can be appended without migration.
/// </summary>
public enum MessageCategory
{
    /// <summary>
    /// Critical system messages (account, consent, security). Always on — cannot opt out.
    /// </summary>
    System = 0,

    /// <summary>
    /// DEPRECATED — replaced by VolunteerUpdates + TeamUpdates. Kept for DB string compatibility.
    /// </summary>
    EventOperations = 1,

    /// <summary>
    /// DEPRECATED — replaced by FacilitatedMessages. Kept for DB string compatibility.
    /// </summary>
    CommunityUpdates = 2,

    /// <summary>
    /// Mailing list, promotions. Default: off.
    /// </summary>
    Marketing = 3,

    /// <summary>
    /// Discount codes, grants. Always on — cannot opt out.
    /// </summary>
    CampaignCodes = 4,

    /// <summary>
    /// User-to-user email via Humans. Default: on.
    /// </summary>
    FacilitatedMessages = 5,

    /// <summary>
    /// Purchase confirmations, event info. Default: on. Locked on when user has a matched ticket order.
    /// </summary>
    Ticketing = 6,

    /// <summary>
    /// Shift changes, schedule updates. Default: on.
    /// </summary>
    VolunteerUpdates = 7,

    /// <summary>
    /// Drive permissions, team member adds/removes. Default: on.
    /// </summary>
    TeamUpdates = 8,
}

public static class MessageCategoryExtensions
{
    /// <summary>
    /// Categories that are deprecated and should not appear in the UI.
    /// Kept in the enum for DB string compatibility only.
    /// </summary>
    public static bool IsDeprecated(this MessageCategory category) => category is
        MessageCategory.EventOperations or MessageCategory.CommunityUpdates;

    /// <summary>
    /// Categories where users cannot opt out — always locked on.
    /// </summary>
    public static bool IsAlwaysOn(this MessageCategory category) => category is
        MessageCategory.System or MessageCategory.CampaignCodes;

    /// <summary>
    /// The active categories shown in the Communication Preferences UI, in display order.
    /// </summary>
    public static IReadOnlyList<MessageCategory> ActiveCategories { get; } = new[]
    {
        MessageCategory.System,
        MessageCategory.CampaignCodes,
        MessageCategory.FacilitatedMessages,
        MessageCategory.Ticketing,
        MessageCategory.VolunteerUpdates,
        MessageCategory.TeamUpdates,
        MessageCategory.Marketing,
    };

    public static string ToDisplayName(this MessageCategory category) => category switch
    {
        MessageCategory.System => "System",
        MessageCategory.EventOperations => "Event Operations",
        MessageCategory.CommunityUpdates => "Community Updates",
        MessageCategory.Marketing => "Marketing",
        MessageCategory.CampaignCodes => "Campaign Codes",
        MessageCategory.FacilitatedMessages => "Facilitated Messages",
        MessageCategory.Ticketing => "Ticketing",
        MessageCategory.VolunteerUpdates => "Volunteer Updates",
        MessageCategory.TeamUpdates => "Team Updates",
        _ => category.ToString(),
    };

    public static string ToDescription(this MessageCategory category) => category switch
    {
        MessageCategory.System => "Critical account, consent, and security notifications. Always on.",
        MessageCategory.EventOperations => "Shift changes, schedule updates, and team notifications.",
        MessageCategory.CommunityUpdates => "General community news and facilitated messages.",
        MessageCategory.Marketing => "Mailing list and promotions.",
        MessageCategory.CampaignCodes => "Discount codes, grants, and campaign redemption codes. Always on.",
        MessageCategory.FacilitatedMessages => "Messages sent to you by other humans via Humans.",
        MessageCategory.Ticketing => "Purchase confirmations and event information.",
        MessageCategory.VolunteerUpdates => "Shift changes, schedule updates, and volunteer notifications.",
        MessageCategory.TeamUpdates => "Drive permissions, team member additions, and removals.",
        _ => string.Empty,
    };
}
```

- [ ] **Step 2: Build to verify compilation**

Run: `dotnet build src/Humans.Domain/Humans.Domain.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Domain/Enums/MessageCategory.cs
git commit -m "feat(#316): expand MessageCategory enum with granular categories"
```

---

### Task 2: Add MessageCategory to EnumStringStabilityTests

**Files:**
- Modify: `tests/Humans.Domain.Tests/Enums/EnumStringStabilityTests.cs`

`MessageCategory` is stored as string in the DB via `HasConversion<string>()` but was never added to the stability test. Add it now to prevent future renames from silently breaking queries.

- [ ] **Step 1: Add MessageCategory to the test data**

Add this entry to the `StringStoredEnumData` property, after the existing entries (before the closing `};`):

```csharp
        {
            typeof(MessageCategory),
            new[]
            {
                "System", "EventOperations", "CommunityUpdates", "Marketing",
                "CampaignCodes", "FacilitatedMessages", "Ticketing", "VolunteerUpdates", "TeamUpdates"
            }
        }
```

Note: The `using Humans.Domain.Enums;` import is already present in this file.

- [ ] **Step 2: Run the test**

Run: `dotnet test tests/Humans.Domain.Tests/ --filter "EnumStringStabilityTests"`
Expected: All tests pass

- [ ] **Step 3: Commit**

```bash
git add tests/Humans.Domain.Tests/Enums/EnumStringStabilityTests.cs
git commit -m "test(#316): add MessageCategory to enum string stability tests"
```

---

### Task 3: Update NotificationSourceMapping

**Files:**
- Modify: `src/Humans.Application/NotificationSourceMapping.cs`

Map existing `NotificationSource` values to the new granular categories.

- [ ] **Step 1: Update the mapping**

```csharp
using Humans.Domain.Enums;

namespace Humans.Application;

/// <summary>
/// Maps NotificationSource to MessageCategory for preference checks.
/// </summary>
public static class NotificationSourceMapping
{
    public static MessageCategory ToMessageCategory(this NotificationSource source) => source switch
    {
        NotificationSource.TeamMemberAdded => MessageCategory.TeamUpdates,
        NotificationSource.ShiftCoverageGap => MessageCategory.VolunteerUpdates,
        NotificationSource.ShiftSignupChange => MessageCategory.VolunteerUpdates,
        NotificationSource.ConsentReviewNeeded => MessageCategory.System,
        NotificationSource.ApplicationSubmitted => MessageCategory.System,
        NotificationSource.SyncError => MessageCategory.System,
        NotificationSource.TermRenewalReminder => MessageCategory.System,
        _ => MessageCategory.System
    };
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/Humans.Application/Humans.Application.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Application/NotificationSourceMapping.cs
git commit -m "feat(#316): update NotificationSourceMapping for granular categories"
```

---

### Task 4: Update CommunicationPreferenceService

**Files:**
- Modify: `src/Humans.Infrastructure/Services/CommunicationPreferenceService.cs`
- Modify: `src/Humans.Application/Interfaces/ICommunicationPreferenceService.cs`

Update the default opt-out map to include all active categories. Update the locked-category guard to include `CampaignCodes`. Add a method to check if a user accepts facilitated messages.

- [ ] **Step 1: Update the interface — add `AcceptsFacilitatedMessagesAsync`**

In `ICommunicationPreferenceService.cs`, add after the existing `IsOptedOutAsync` method:

```csharp
    /// <summary>
    /// Returns whether a user accepts facilitated messages (i.e. has NOT opted out of FacilitatedMessages).
    /// Used to gate the Send Message function.
    /// </summary>
    Task<bool> AcceptsFacilitatedMessagesAsync(
        Guid userId, CancellationToken cancellationToken = default);
```

- [ ] **Step 2: Update the service — default map and locked categories**

In `CommunicationPreferenceService.cs`, replace the `DefaultOptedOut` dictionary:

```csharp
    private static readonly Dictionary<MessageCategory, bool> DefaultOptedOut = new()
    {
        [MessageCategory.System] = false,
        [MessageCategory.CampaignCodes] = false,
        [MessageCategory.FacilitatedMessages] = false,
        [MessageCategory.Ticketing] = false,
        [MessageCategory.VolunteerUpdates] = false,
        [MessageCategory.TeamUpdates] = false,
        [MessageCategory.Marketing] = true,
    };
```

Note: `EventOperations` and `CommunityUpdates` are deliberately omitted — they're deprecated. Existing rows stay in the DB but won't be auto-created for new users.

- [ ] **Step 3: Update the locked-category guard in both `UpdatePreferenceAsync` overloads**

In both `UpdatePreferenceAsync` methods, change:
```csharp
        if (category == MessageCategory.System)
```
to:
```csharp
        if (category.IsAlwaysOn())
```

And update the log message from `"Attempted to change System preference"` to `"Attempted to change always-on preference {Category}"` (include `category` in the log).

- [ ] **Step 4: Update `IsOptedOutAsync` to handle always-on categories**

Change:
```csharp
        if (category == MessageCategory.System)
            return false;
```
to:
```csharp
        if (category.IsAlwaysOn())
            return false;
```

- [ ] **Step 5: Implement `AcceptsFacilitatedMessagesAsync`**

Add to the service class:

```csharp
    public async Task<bool> AcceptsFacilitatedMessagesAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        return !await IsOptedOutAsync(userId, MessageCategory.FacilitatedMessages, cancellationToken);
    }
```

- [ ] **Step 6: Build and run existing tests**

Run: `dotnet build src/Humans.Infrastructure/Humans.Infrastructure.csproj && dotnet test tests/Humans.Application.Tests/ --filter "CommunicationPreference or NotificationService or Outbox"`
Expected: Build succeeded, all tests pass

- [ ] **Step 7: Commit**

```bash
git add src/Humans.Application/Interfaces/ICommunicationPreferenceService.cs src/Humans.Infrastructure/Services/CommunicationPreferenceService.cs
git commit -m "feat(#316): update CommunicationPreferenceService for granular categories and facilitated message check"
```

---

### Task 5: Data Migration — Split Old Categories

**Files:**
- Create: new EF migration file (generated)

Write a SQL data migration that:
1. For each `EventOperations` row: INSERT a `TeamUpdates` clone, then UPDATE the original to `VolunteerUpdates`
2. UPDATE all `CommunityUpdates` rows to `FacilitatedMessages`

No schema changes — this is data-only.

- [ ] **Step 1: Create the migration**

Run: `dotnet ef migrations add SplitCommunicationCategories --project src/Humans.Infrastructure --startup-project src/Humans.Web`

- [ ] **Step 2: Replace the generated migration body**

The migration file will be at `src/Humans.Infrastructure/Migrations/{timestamp}_SplitCommunicationCategories.cs`. Replace the `Up` and `Down` methods with:

```csharp
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // 1. Clone EventOperations rows as TeamUpdates (same OptedOut + InboxEnabled)
        migrationBuilder.Sql(@"
            INSERT INTO communication_preferences (""Id"", ""UserId"", ""Category"", ""OptedOut"", ""InboxEnabled"", ""UpdatedAt"", ""UpdateSource"")
            SELECT gen_random_uuid(), ""UserId"", 'TeamUpdates', ""OptedOut"", ""InboxEnabled"", ""UpdatedAt"", 'DataMigration'
            FROM communication_preferences
            WHERE ""Category"" = 'EventOperations'
            ON CONFLICT (""UserId"", ""Category"") DO NOTHING;
        ");

        // 2. Rename EventOperations → VolunteerUpdates
        migrationBuilder.Sql(@"
            UPDATE communication_preferences
            SET ""Category"" = 'VolunteerUpdates', ""UpdateSource"" = 'DataMigration'
            WHERE ""Category"" = 'EventOperations';
        ");

        // 3. Rename CommunityUpdates → FacilitatedMessages
        migrationBuilder.Sql(@"
            UPDATE communication_preferences
            SET ""Category"" = 'FacilitatedMessages', ""UpdateSource"" = 'DataMigration'
            WHERE ""Category"" = 'CommunityUpdates';
        ");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Reverse: FacilitatedMessages → CommunityUpdates
        migrationBuilder.Sql(@"
            UPDATE communication_preferences
            SET ""Category"" = 'CommunityUpdates', ""UpdateSource"" = 'DataMigration'
            WHERE ""Category"" = 'FacilitatedMessages';
        ");

        // Reverse: VolunteerUpdates → EventOperations
        migrationBuilder.Sql(@"
            UPDATE communication_preferences
            SET ""Category"" = 'EventOperations', ""UpdateSource"" = 'DataMigration'
            WHERE ""Category"" = 'VolunteerUpdates';
        ");

        // Remove TeamUpdates rows created by migration
        migrationBuilder.Sql(@"
            DELETE FROM communication_preferences
            WHERE ""Category"" = 'TeamUpdates' AND ""UpdateSource"" = 'DataMigration';
        ");
    }
```

- [ ] **Step 3: Review the generated Designer.cs file**

Open the `.Designer.cs` file and verify:
- The namespace is `Humans.Infrastructure.Migrations`
- The `[DbContext(typeof(HumansDbContext))]` attribute is present
- The `[Migration("...")]` attribute has the correct timestamp

- [ ] **Step 4: Run the EF migration reviewer agent**

Run the EF migration reviewer per `.claude/agents/ef-migration-reviewer.md` before committing. It must pass with no CRITICAL issues.

- [ ] **Step 5: Build**

Run: `dotnet build Humans.slnx`
Expected: Build succeeded

- [ ] **Step 6: Commit**

```bash
git add src/Humans.Infrastructure/Migrations/
git commit -m "feat(#316): data migration — split EventOperations and rename CommunityUpdates"
```

---

### Task 6: Update OutboxEmailService Category References

**Files:**
- Modify: `src/Humans.Infrastructure/Services/OutboxEmailService.cs`

The `SendAddedToTeamAsync` method currently uses `MessageCategory.EventOperations`. Update it to the new category.

- [ ] **Step 1: Find and update the category reference**

In `OutboxEmailService.cs`, find the call at approximately line 194:
```csharp
            category: MessageCategory.EventOperations);
```
Change to:
```csharp
            category: MessageCategory.TeamUpdates);
```

Search the rest of the file for any other `MessageCategory.EventOperations` or `MessageCategory.CommunityUpdates` references and update them similarly. (Check `grep -n "EventOperations\|CommunityUpdates" src/Humans.Infrastructure/Services/OutboxEmailService.cs` first.)

- [ ] **Step 2: Update CampaignService**

In `src/Humans.Infrastructure/Services/CampaignService.cs`, the campaign send uses `MessageCategory.Marketing` for unsubscribe headers. Campaign codes should now use `MessageCategory.CampaignCodes` instead — but only if the campaign is a code distribution, not a promotional email. Since this distinction doesn't exist yet in the data model, leave it as `MessageCategory.Marketing` for now and note this as a future enhancement. **No change needed here.**

- [ ] **Step 3: Update the `SendFacilitatedMessageAsync` call in OutboxEmailService**

Search for `SendFacilitatedMessageAsync` in OutboxEmailService. If it passes a `MessageCategory`, update from `CommunityUpdates` to `FacilitatedMessages`. If it doesn't pass one, add `category: MessageCategory.FacilitatedMessages` to the `EnqueueAsync` call.

- [ ] **Step 4: Build**

Run: `dotnet build src/Humans.Infrastructure/Humans.Infrastructure.csproj`
Expected: Build succeeded

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Infrastructure/Services/OutboxEmailService.cs
git commit -m "feat(#316): update email service category references to granular categories"
```

---

### Task 7: Redesign View Models

**Files:**
- Modify: `src/Humans.Web/Models/CommunicationPreferenceViewModels.cs`

Redesign the view model for the table layout with positive framing.

- [ ] **Step 1: Rewrite the view models**

```csharp
using Humans.Domain.Enums;

namespace Humans.Web.Models;

public class CommunicationPreferencesViewModel
{
    public List<CategoryPreferenceItem> Categories { get; set; } = [];
}

public class CategoryPreferenceItem
{
    public MessageCategory Category { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Positive framing: true = user receives email for this category.
    /// Stored as !OptedOut in the entity.
    /// </summary>
    public bool EmailEnabled { get; set; } = true;

    /// <summary>
    /// Whether in-app alerts are enabled for this category.
    /// Maps directly to InboxEnabled on the entity.
    /// </summary>
    public bool AlertEnabled { get; set; } = true;

    /// <summary>
    /// Whether the user can change the email preference for this category.
    /// False for always-on categories (System, CampaignCodes) and locked ticketing.
    /// </summary>
    public bool EmailEditable { get; set; }

    /// <summary>
    /// Whether the user can change the alert preference for this category.
    /// </summary>
    public bool AlertEditable { get; set; }

    /// <summary>
    /// Optional note shown below the category (e.g., "Locked — you have a ticket order for 2026").
    /// </summary>
    public string? Note { get; set; }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src/Humans.Web/Humans.Web.csproj`
Expected: Build succeeded (will have warnings about unused `OptedOut`/`IsEditable` — that's fine, the view and controller haven't been updated yet)

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/Models/CommunicationPreferenceViewModels.cs
git commit -m "feat(#316): redesign CommunicationPreference view models for table layout"
```

---

### Task 8: Update ProfileController

**Files:**
- Modify: `src/Humans.Web/Controllers/ProfileController.cs`

Update the controller actions and helper methods for the renamed page, positive framing, ticketing lock check, and both-channel saving.

- [ ] **Step 1: Update the GET action — rename route and add ticketing check**

Replace the existing GET action (lines ~858–875):

```csharp
    [HttpGet("Me/CommunicationPreferences")]
    public async Task<IActionResult> CommunicationPreferences()
    {
        try
        {
            var user = await GetCurrentUserAsync();
            if (user is null)
                return NotFound();

            return View(await BuildCommunicationPreferencesViewModelAsync(user.Id));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load communication preferences");
            SetError("Failed to load communication preferences.");
            return RedirectToAction(nameof(Me));
        }
    }
```

- [ ] **Step 2: Update the POST action — save both channels with positive framing**

Replace the existing POST action (lines ~877–906):

```csharp
    [HttpPost("Me/CommunicationPreferences")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CommunicationPreferences(CommunicationPreferencesViewModel model)
    {
        try
        {
            var user = await GetCurrentUserAsync();
            if (user is null)
                return NotFound();

            foreach (var item in model.Categories)
            {
                if (item.Category.IsAlwaysOn())
                    continue;

                // Invert EmailEnabled → OptedOut for storage
                await _commPrefService.UpdatePreferenceAsync(
                    user.Id, item.Category, optedOut: !item.EmailEnabled, inboxEnabled: item.AlertEnabled, "Profile");
            }

            SetSuccess(_localizer["Profile_Updated"].Value);
            return RedirectToAction(nameof(CommunicationPreferences));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save communication preferences");
            SetError("Failed to save communication preferences.");
            PopulateCommunicationPreferenceMetadata(model);
            return View(model);
        }
    }
```

- [ ] **Step 3: Add a redirect from the old route**

Add below the new actions, so old bookmarks still work:

```csharp
    [HttpGet("Me/Notifications")]
    public IActionResult Notifications() => RedirectToActionPermanent(nameof(CommunicationPreferences));
```

- [ ] **Step 4: Update `BuildCommunicationPreferencesViewModelAsync`**

Replace the helper method (lines ~1561–1576):

```csharp
    private async Task<CommunicationPreferencesViewModel> BuildCommunicationPreferencesViewModelAsync(Guid userId)
    {
        var prefs = await _commPrefService.GetPreferencesAsync(userId);
        var prefsByCategory = prefs.ToDictionary(p => p.Category);

        // Check if user has a matched ticket order (locks ticketing preference)
        var hasTicketOrder = await _db.TicketOrders
            .AnyAsync(o => o.MatchedUserId == userId);

        var categories = new List<CategoryPreferenceItem>();

        foreach (var category in MessageCategoryExtensions.ActiveCategories)
        {
            var pref = prefsByCategory.GetValueOrDefault(category);
            var isAlwaysOn = category.IsAlwaysOn();
            var isTicketingLocked = category == MessageCategory.Ticketing && hasTicketOrder;

            categories.Add(new CategoryPreferenceItem
            {
                Category = category,
                DisplayName = category == MessageCategory.Ticketing
                    ? $"Ticketing — {DateTime.UtcNow.Year}"
                    : category.ToDisplayName(),
                Description = category.ToDescription(),
                EmailEnabled = pref is null ? true : !pref.OptedOut,
                AlertEnabled = pref?.InboxEnabled ?? true,
                EmailEditable = !isAlwaysOn && !isTicketingLocked,
                AlertEditable = !isAlwaysOn && !isTicketingLocked,
                Note = isTicketingLocked ? "Locked — you have a ticket order for this year" : null,
            });
        }

        return new CommunicationPreferencesViewModel { Categories = categories };
    }
```

Note: This uses `_db` (the `HumansDbContext` field). Check that the ProfileController already injects it — if not, it will be available as the EF context. Check the existing constructor for the field name — it might be `_dbContext` or `_db`. Use whichever is already there.

- [ ] **Step 5: Update `PopulateCommunicationPreferenceMetadata`**

Replace the helper method (lines ~1578–1586):

```csharp
    private static void PopulateCommunicationPreferenceMetadata(CommunicationPreferencesViewModel model)
    {
        foreach (var item in model.Categories)
        {
            item.DisplayName = item.Category == MessageCategory.Ticketing
                ? $"Ticketing — {DateTime.UtcNow.Year}"
                : item.Category.ToDisplayName();
            item.Description = item.Category.ToDescription();
        }
    }
```

- [ ] **Step 6: Build**

Run: `dotnet build src/Humans.Web/Humans.Web.csproj`
Expected: Build succeeded

- [ ] **Step 7: Commit**

```bash
git add src/Humans.Web/Controllers/ProfileController.cs
git commit -m "feat(#316): update ProfileController for communication preferences redesign"
```

---

### Task 9: Rewrite the View as a Table

**Files:**
- Create: `src/Humans.Web/Views/Profile/CommunicationPreferences.cshtml` (rename from Notifications.cshtml)
- Delete: `src/Humans.Web/Views/Profile/Notifications.cshtml`

- [ ] **Step 1: Create the new view file**

```html
@model Humans.Web.Models.CommunicationPreferencesViewModel
@inject Microsoft.Extensions.Localization.IStringLocalizer<Humans.Web.SharedResource> Localizer
@{
    ViewData["Title"] = "Communication Preferences";
}

<div class="row">
    <div class="col-12" style="max-width: 900px;">
        <nav aria-label="breadcrumb">
            <ol class="breadcrumb">
                <li class="breadcrumb-item"><a asp-action="Index">Profile</a></li>
                <li class="breadcrumb-item active" aria-current="page">Communication Preferences</li>
            </ol>
        </nav>

        <h1>Communication Preferences</h1>
        <p class="text-muted">Choose which communications you receive and how.</p>

        <vc:temp-data-alerts />

        <form asp-action="CommunicationPreferences" method="post">
            @Html.AntiForgeryToken()

            <div class="card mb-4">
                <div class="table-responsive">
                    <table class="table table-hover mb-0 align-middle">
                        <thead>
                            <tr>
                                <th style="min-width: 250px;">Category</th>
                                <th class="text-center" style="width: 100px;">Email</th>
                                <th class="text-center" style="width: 100px;">Alert</th>
                            </tr>
                        </thead>
                        <tbody>
                            @for (var i = 0; i < Model.Categories.Count; i++)
                            {
                                var item = Model.Categories[i];
                                <input type="hidden" name="Categories[@i].Category" value="@item.Category" />

                                <tr class="@(!item.EmailEditable ? "table-light" : "")">
                                    <td>
                                        <div class="fw-semibold">@item.DisplayName</div>
                                        <div class="form-text mb-0">@item.Description</div>
                                        @if (item.Note is not null)
                                        {
                                            <div class="form-text text-warning-emphasis mb-0">
                                                <i class="fa-solid fa-lock fa-xs me-1"></i>@item.Note
                                            </div>
                                        }
                                    </td>
                                    <td class="text-center">
                                        @if (item.EmailEditable)
                                        {
                                            <input type="hidden" name="Categories[@i].EmailEnabled" value="false" />
                                            <input type="checkbox"
                                                   id="Categories_@(i)_EmailEnabled"
                                                   name="Categories[@i].EmailEnabled"
                                                   value="true"
                                                   class="form-check-input"
                                                   @(item.EmailEnabled ? "checked" : null) />
                                        }
                                        else
                                        {
                                            <input type="checkbox"
                                                   class="form-check-input"
                                                   disabled
                                                   checked />
                                        }
                                    </td>
                                    <td class="text-center">
                                        @if (item.AlertEditable)
                                        {
                                            <input type="hidden" name="Categories[@i].AlertEnabled" value="false" />
                                            <input type="checkbox"
                                                   id="Categories_@(i)_AlertEnabled"
                                                   name="Categories[@i].AlertEnabled"
                                                   value="true"
                                                   class="form-check-input"
                                                   @(item.AlertEnabled ? "checked" : null) />
                                        }
                                        else
                                        {
                                            <input type="checkbox"
                                                   class="form-check-input"
                                                   disabled
                                                   checked />
                                        }
                                    </td>
                                </tr>
                            }
                        </tbody>
                    </table>
                </div>
            </div>

            <div class="d-flex justify-content-between">
                <a asp-action="Index" class="btn btn-outline-secondary">Back to Profile</a>
                <button type="submit" class="btn btn-primary">Save</button>
            </div>
        </form>
    </div>
</div>
```

- [ ] **Step 2: Delete the old view**

```bash
rm src/Humans.Web/Views/Profile/Notifications.cshtml
```

- [ ] **Step 3: Build**

Run: `dotnet build src/Humans.Web/Humans.Web.csproj`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/Views/Profile/CommunicationPreferences.cshtml
git rm src/Humans.Web/Views/Profile/Notifications.cshtml
git commit -m "feat(#316): rewrite notification settings view as Communication Preferences table"
```

---

### Task 10: Update Profile Quick Link

**Files:**
- Modify: `src/Humans.Web/Views/Profile/Index.cshtml:157`

- [ ] **Step 1: Update the quick link**

Find line 157:
```html
                    <a asp-controller="Profile" asp-action="Notifications" class="list-group-item list-group-item-action">Notifications</a>
```
Replace with:
```html
                    <a asp-controller="Profile" asp-action="CommunicationPreferences" class="list-group-item list-group-item-action">Communication Preferences</a>
```

- [ ] **Step 2: Search for any other "Notifications" links pointing to the old route**

Run: `grep -rn 'asp-action="Notifications"' src/Humans.Web/Views/`

Update any other references found.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/Views/Profile/Index.cshtml
git commit -m "feat(#316): rename Profile quick link to Communication Preferences"
```

---

### Task 11: Block SendMessage When Facilitated Messages Opted Out

**Files:**
- Modify: `src/Humans.Web/Controllers/ProfileController.cs` (SendMessage actions)
- Modify: `src/Humans.Web/ViewComponents/ProfileCardViewComponent.cs` (hide button)

- [ ] **Step 1: Add opt-out check to GET SendMessage**

In the GET `SendMessage` action (line ~1039), after the `targetUser` null check and before creating the view model, add:

```csharp
        if (!await _commPrefService.AcceptsFacilitatedMessagesAsync(id))
        {
            SetError("This human has opted out of receiving messages.");
            return RedirectToAction(nameof(ViewProfile), new { id });
        }
```

- [ ] **Step 2: Add opt-out check to POST SendMessage**

In the POST `SendMessage` action (line ~1062), after the `targetUser` null check and before model validation, add the same check:

```csharp
        if (!await _commPrefService.AcceptsFacilitatedMessagesAsync(id))
        {
            SetError("This human has opted out of receiving messages.");
            return RedirectToAction(nameof(ViewProfile), new { id });
        }
```

- [ ] **Step 3: Hide the Send Message button on profile cards**

In `ProfileCardViewComponent.cs`, the `CanSendMessage` property is set at line ~181. The component needs access to `ICommunicationPreferenceService`. Add it to the constructor:

```csharp
    private readonly ICommunicationPreferenceService _commPrefService;
```

Add the parameter to the constructor and assign it.

Then update the `CanSendMessage` logic (line ~181). Change:
```csharp
            CanSendMessage = !isOwnProfile
                && !visibleEmails.Any(e => e.Visibility >= ContactFieldVisibility.AllActiveProfiles)
```
to:
```csharp
            CanSendMessage = !isOwnProfile
                && !visibleEmails.Any(e => e.Visibility >= ContactFieldVisibility.AllActiveProfiles)
                && await _commPrefService.AcceptsFacilitatedMessagesAsync(userId)
```

Note: The `InvokeAsync` method signature is already `async Task<IViewComponentResult>`, so `await` works here.

- [ ] **Step 4: Build**

Run: `dotnet build src/Humans.Web/Humans.Web.csproj`
Expected: Build succeeded

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Web/Controllers/ProfileController.cs src/Humans.Web/ViewComponents/ProfileCardViewComponent.cs
git commit -m "feat(#316): block SendMessage when recipient has opted out of facilitated messages"
```

---

### Task 12: Write Tests

**Files:**
- Modify: `tests/Humans.Application.Tests/Services/NotificationServiceTests.cs` (or create a new test file)

Write tests for the key new behaviors.

- [ ] **Step 1: Create CommunicationPreferenceServiceTests**

Create `tests/Humans.Application.Tests/Services/CommunicationPreferenceServiceTests.cs`:

```csharp
using AwesomeAssertions;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using Xunit;

namespace Humans.Application.Tests.Services;

public class CommunicationPreferenceServiceTests : IDisposable
{
    private readonly HumansDbContext _db;
    private readonly FakeClock _clock;
    private readonly CommunicationPreferenceService _service;

    public CommunicationPreferenceServiceTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _db = new HumansDbContext(options);
        _clock = new FakeClock(Instant.FromUtc(2026, 4, 4, 12, 0));

        var dataProtection = new EphemeralDataProtectionProvider();
        var auditLog = new StubAuditLogService();
        var emailSettings = Options.Create(new Humans.Infrastructure.Configuration.EmailSettings
        {
            BaseUrl = "https://test.example.com"
        });

        _service = new CommunicationPreferenceService(
            _db, dataProtection, _clock, auditLog, emailSettings,
            NullLogger<CommunicationPreferenceService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task GetPreferencesAsync_CreatesDefaultsForActiveCategories()
    {
        var userId = Guid.NewGuid();
        var prefs = await _service.GetPreferencesAsync(userId);

        // Should create defaults for all active (non-deprecated) categories
        prefs.Should().HaveCount(7);
        prefs.Select(p => p.Category).Should().NotContain(MessageCategory.EventOperations);
        prefs.Select(p => p.Category).Should().NotContain(MessageCategory.CommunityUpdates);
    }

    [Fact]
    public async Task GetPreferencesAsync_MarketingDefaultsToOptedOut()
    {
        var userId = Guid.NewGuid();
        var prefs = await _service.GetPreferencesAsync(userId);

        var marketing = prefs.Single(p => p.Category == MessageCategory.Marketing);
        marketing.OptedOut.Should().BeTrue();
    }

    [Fact]
    public async Task UpdatePreferenceAsync_RejectsAlwaysOnCategories()
    {
        var userId = Guid.NewGuid();

        await _service.UpdatePreferenceAsync(userId, MessageCategory.System, true, "Test");
        await _service.UpdatePreferenceAsync(userId, MessageCategory.CampaignCodes, true, "Test");

        var prefs = await _service.GetPreferencesAsync(userId);
        prefs.Single(p => p.Category == MessageCategory.System).OptedOut.Should().BeFalse();
        prefs.Single(p => p.Category == MessageCategory.CampaignCodes).OptedOut.Should().BeFalse();
    }

    [Fact]
    public async Task IsOptedOutAsync_ReturnsFalseForAlwaysOnCategories()
    {
        var userId = Guid.NewGuid();

        (await _service.IsOptedOutAsync(userId, MessageCategory.System)).Should().BeFalse();
        (await _service.IsOptedOutAsync(userId, MessageCategory.CampaignCodes)).Should().BeFalse();
    }

    [Fact]
    public async Task AcceptsFacilitatedMessagesAsync_ReturnsTrueByDefault()
    {
        var userId = Guid.NewGuid();
        (await _service.AcceptsFacilitatedMessagesAsync(userId)).Should().BeTrue();
    }

    [Fact]
    public async Task AcceptsFacilitatedMessagesAsync_ReturnsFalseWhenOptedOut()
    {
        var userId = Guid.NewGuid();
        await _service.UpdatePreferenceAsync(userId, MessageCategory.FacilitatedMessages, true, "Test");

        (await _service.AcceptsFacilitatedMessagesAsync(userId)).Should().BeFalse();
    }
}

/// <summary>Stub for tests — does nothing.</summary>
file class StubAuditLogService : Humans.Application.Interfaces.IAuditLogService
{
    public Task LogAsync(AuditAction action, string entityType, Guid entityId, string description,
        string? actorId = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task LogAsync(AuditAction action, string entityType, Guid entityId, string description,
        Guid? performedByUserId, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
```

Note: The `StubAuditLogService` shape must match the actual `IAuditLogService` interface. Check the interface for the exact method signatures before writing the stub — adjust the overloads to match.

- [ ] **Step 2: Run the tests**

Run: `dotnet test tests/Humans.Application.Tests/ --filter "CommunicationPreferenceServiceTests"`
Expected: All 6 tests pass

- [ ] **Step 3: Commit**

```bash
git add tests/Humans.Application.Tests/Services/CommunicationPreferenceServiceTests.cs
git commit -m "test(#316): add CommunicationPreferenceService tests for granular categories"
```

---

### Task 13: Update Feature Documentation

**Files:**
- Create or modify: `docs/features/communication-preferences.md`

- [ ] **Step 1: Check if a feature doc already exists**

Run: `ls docs/features/ | grep -i "communicat\|notif\|preference"`

If one exists, update it. If not, create `docs/features/communication-preferences.md`.

- [ ] **Step 2: Write the feature doc**

Include:
- Business context (GDPR/CAN-SPAM, user control over communications)
- The category table with Email/Alert columns
- Always-on rules (System, CampaignCodes)
- Ticketing per-year locking logic
- Facilitated messages opt-out → blocks SendMessage
- Data model: `CommunicationPreference` entity with `OptedOut`/`InboxEnabled`
- Routes: `GET/POST /Me/CommunicationPreferences` (redirect from old `/Me/Notifications`)
- Migration notes: EventOperations split, CommunityUpdates renamed

- [ ] **Step 3: Commit**

```bash
git add docs/features/communication-preferences.md
git commit -m "docs(#316): add Communication Preferences feature documentation"
```

---

### Task 14: Final Build + Test

- [ ] **Step 1: Full solution build**

Run: `dotnet build Humans.slnx`
Expected: Build succeeded with 0 errors

- [ ] **Step 2: Full test suite**

Run: `dotnet test Humans.slnx`
Expected: All tests pass

- [ ] **Step 3: Format check**

Run: `dotnet format Humans.slnx --verify-no-changes`
Expected: No formatting issues (fix any that appear)

- [ ] **Step 4: Squash and create PR**

Follow the standard git workflow: create feature branch, push, create PR targeting `main` on `peterdrier/Humans`.
