# Session 1: Auth Fix + Google Sync Fixes — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix Google sign-in 500 error (#172) and three Google sync issues: googlemail handling (#193), duplicate key on group link (#173), and group settings remediation with domain-wide visibility (#195).

**Architecture:** Two independent batches — Batch 1 (auth, single worktree) and Batch 2 (google-sync, single worktree). Batch 2 issues share `GoogleWorkspaceSyncService.cs` so they must be one branch. No EF Core migrations needed.

**Tech Stack:** ASP.NET Core 9, Google Admin SDK, Google Groups Settings API, Cloud Identity API, EF Core 9, Razor views, Bootstrap 5.

**Spec:** `docs/superpowers/specs/2026-03-23-session1-auth-google-sync-design.md`

---

## Batch 1: #172 — Google Sign-In 500 Fix

### Task 1: Add OnRemoteFailure handler to Google OAuth

**Files:**
- Modify: `src/Humans.Web/Program.cs:124-134`

- [ ] **Step 1: Add OnRemoteFailure event handler**

In `Program.cs`, inside the `.AddGoogle(options => { ... })` block (after line 133), add:

```csharp
options.Events = new Microsoft.AspNetCore.Authentication.OAuth.OAuthEvents
{
    OnRemoteFailure = context =>
    {
        var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("GoogleOAuth");
        logger.LogWarning(context.Failure, "Google sign-in failed: {Error}", context.Failure?.Message);

        context.Response.Redirect("/Account/Login?error=sign-in-failed");
        context.HandleResponse();
        return Task.CompletedTask;
    }
};
```

- [ ] **Step 2: Verify it compiles**

Run: `dotnet build src/Humans.Web/Humans.Web.csproj`
Expected: Build succeeded.

### Task 2: Add error display to Login view

**Files:**
- Modify: `src/Humans.Web/Views/Account/Login.cshtml`

- [ ] **Step 1: Add error alert above the login card**

In `Login.cshtml`, immediately after the opening `<div class="row justify-content-center">` (line 8), before the `<div class="col-md-6">`, add:

```html
@if (Context.Request.Query.ContainsKey("error"))
{
    <div class="col-md-6 mb-0">
        <div class="alert alert-warning alert-dismissible fade show mt-5 mb-0" role="alert">
            <i class="fa-solid fa-triangle-exclamation me-1"></i>
            @Localizer["Login_SignInFailed"]
            <button type="button" class="btn-close" data-bs-dismiss="alert" aria-label="Close"></button>
        </div>
    </div>
</div>
<div class="row justify-content-center">
```

- [ ] **Step 2: Add localization key**

Add `Login_SignInFailed` to the shared resource files. English value: `"Sign-in failed. This can happen if you took too long or your browser blocked cookies. Please try again."`

Search for existing resource files:
```bash
grep -rl "Login_Title" src/Humans.Web/Resources/
```
Add the key to each locale file found.

- [ ] **Step 3: Verify it compiles**

Run: `dotnet build src/Humans.Web/Humans.Web.csproj`
Expected: Build succeeded.

- [ ] **Step 4: Commit Batch 1**

```bash
git add src/Humans.Web/Program.cs src/Humans.Web/Views/Account/Login.cshtml src/Humans.Web/Resources/
git commit -m "fix: handle missing correlation cookie gracefully on Google sign-in (#172)

Add OnRemoteFailure handler to Google OAuth config that redirects to
login page with user-friendly error instead of 500. Add dismissible
alert to Login view with localized error message."
```

---

## Batch 2: Google Sync Fixes (#193, #173, #195)

### Task 3: Add NormalizeForComparison to EmailNormalization (#193 Part 2)

**Files:**
- Modify: `src/Humans.Domain/Helpers/EmailNormalization.cs`

- [ ] **Step 1: Add NormalizeForComparison method**

Add below the existing `Canonicalize` method:

```csharp
/// <summary>
/// Normalizes an email for comparison only (never for storage).
/// Lowercases and maps @googlemail.com ↔ @gmail.com so they compare equal.
/// </summary>
public static string NormalizeForComparison(string email)
{
    if (string.IsNullOrEmpty(email))
        return email;

    var lower = email.ToLowerInvariant();

    if (lower.EndsWith("@googlemail.com"))
        return string.Concat(lower.AsSpan(0, lower.Length - "@googlemail.com".Length), "@gmail.com");

    return lower;
}
```

- [ ] **Step 2: Add a comparison helper**

Add a static method for use in LINQ and HashSet scenarios:

```csharp
/// <summary>
/// Compares two email addresses for equivalence, treating @googlemail.com and @gmail.com as the same domain.
/// </summary>
public static bool EmailsMatch(string? a, string? b)
{
    if (a is null || b is null) return a == b;
    return string.Equals(NormalizeForComparison(a), NormalizeForComparison(b), StringComparison.Ordinal);
}
```

- [ ] **Step 3: Verify it compiles**

Run: `dotnet build src/Humans.Domain/Humans.Domain.csproj`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Domain/Helpers/EmailNormalization.cs
git commit -m "feat: add NormalizeForComparison to EmailNormalization (#193)

Comparison-only normalization that lowercases and maps googlemail↔gmail.
Never persisted — only used at sync comparison boundaries."
```

### Task 4: Remove canonicalization from storage paths (#193 Part 1)

**Files:**
- Modify: `src/Humans.Infrastructure/Services/UserEmailService.cs:100,336`
- Modify: `src/Humans.Web/Controllers/AccountController.cs:95`

- [ ] **Step 1: Remove Canonicalize from UserEmailService.AddEmailAsync**

In `UserEmailService.cs` line 100, change:
```csharp
email = EmailNormalization.Canonicalize(email.Trim());
```
to:
```csharp
email = email.Trim();
```

- [ ] **Step 2: Remove Canonicalize from UserEmailService.AddOAuthEmailAsync**

In `UserEmailService.cs` line 336, change:
```csharp
email = EmailNormalization.Canonicalize(email);
```
to just remove this line entirely (email is already passed in from Google, no trimming needed).

- [ ] **Step 3: Remove Canonicalize from AccountController.ExternalLoginCallback**

In `AccountController.cs` line 95, change:
```csharp
var email = EmailNormalization.Canonicalize(
    info.Principal.FindFirstValue(ClaimTypes.Email) ?? string.Empty);
```
to:
```csharp
var email = info.Principal.FindFirstValue(ClaimTypes.Email) ?? string.Empty;
```

- [ ] **Step 4: Verify it compiles**

Run: `dotnet build Humans.slnx`
Expected: Build succeeded. Canonicalize may now be unused — if so, remove it or mark it `[Obsolete]`.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Infrastructure/Services/UserEmailService.cs src/Humans.Web/Controllers/AccountController.cs
git commit -m "fix: stop canonicalizing emails on storage (#193)

Store real email addresses from Google instead of mapping
googlemail→gmail. Prevents OAuth lookup failures and false sync drift."
```

### Task 5: Use NormalizeForComparison in sync comparisons (#193 Part 2 continued)

**Files:**
- Modify: `src/Humans.Infrastructure/Services/GoogleWorkspaceSyncService.cs:817-818,823,972-973,1000-1013`

The challenge: `HashSet<string>` with `StringComparer.OrdinalIgnoreCase` doesn't handle googlemail normalization. We need a custom comparer.

- [ ] **Step 1: Add a private NormalizingEmailComparer class**

At the bottom of `GoogleWorkspaceSyncService.cs` (before the closing `}`), or in `EmailNormalization.cs`, add:

```csharp
/// <summary>
/// IEqualityComparer that normalizes googlemail↔gmail before comparing.
/// </summary>
public sealed class NormalizingEmailComparer : IEqualityComparer<string>
{
    public static readonly NormalizingEmailComparer Instance = new();

    public bool Equals(string? x, string? y) => EmailNormalization.EmailsMatch(x, y);

    public int GetHashCode(string obj) => EmailNormalization.NormalizeForComparison(obj).GetHashCode();
}
```

Place this in `src/Humans.Domain/Helpers/EmailNormalization.cs` since it's a domain-level comparison concern.

- [ ] **Step 2: Update SyncGroupResourceAsync HashSets**

In `GoogleWorkspaceSyncService.cs`, update lines ~817-818:
```csharp
// Before:
var expectedEmails = new HashSet<string>(
    expectedMembers.Select(m => m.Email!), StringComparer.OrdinalIgnoreCase);
```
```csharp
// After:
var expectedEmails = new HashSet<string>(
    expectedMembers.Select(m => m.Email!), NormalizingEmailComparer.Instance);
```

Update line ~823:
```csharp
// Before:
var currentEmails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
```
```csharp
// After:
var currentEmails = new HashSet<string>(NormalizingEmailComparer.Instance);
```

Update line ~825 (membershipNames dictionary):
```csharp
// Before:
var membershipNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
```
```csharp
// After:
var membershipNames = new Dictionary<string, string>(NormalizingEmailComparer.Instance);
```

- [ ] **Step 3: Update SyncDriveResourceGroupAsync HashSets**

In `GoogleWorkspaceSyncService.cs`, update lines ~972-973:
```csharp
// Before:
var membersByEmail = new Dictionary<string, (string DisplayName, List<string> TeamNames)>(
    StringComparer.OrdinalIgnoreCase);
```
```csharp
// After:
var membersByEmail = new Dictionary<string, (string DisplayName, List<string> TeamNames)>(
    NormalizingEmailComparer.Instance);
```

Update lines ~1000-1004:
```csharp
// Before:
var allEmails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
var directEmails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
var roleByEmail = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
```
```csharp
// After:
var allEmails = new HashSet<string>(NormalizingEmailComparer.Instance);
var directEmails = new HashSet<string>(NormalizingEmailComparer.Instance);
var roleByEmail = new Dictionary<string, string>(NormalizingEmailComparer.Instance);
```

- [ ] **Step 4: Verify it compiles**

Run: `dotnet build Humans.slnx`
Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Domain/Helpers/EmailNormalization.cs src/Humans.Infrastructure/Services/GoogleWorkspaceSyncService.cs
git commit -m "fix: normalize googlemail↔gmail at sync comparison boundaries (#193)

Use NormalizingEmailComparer for all email HashSets/Dictionaries in
Group and Drive sync. Prevents false extra/missing members when a
user's stored email domain differs from their Google API response."
```

### Task 6: Add email backfill review admin page (#193 Part 3)

**Files:**
- Modify: `src/Humans.Web/Controllers/AdminController.cs`
- Create: `src/Humans.Web/Views/Admin/EmailBackfillReview.cshtml`
- Modify: `src/Humans.Application/Interfaces/IGoogleSyncService.cs`
- Modify: `src/Humans.Infrastructure/Services/GoogleWorkspaceSyncService.cs`
- Modify: `src/Humans.Infrastructure/Services/StubGoogleSyncService.cs`

- [ ] **Step 0: Add GetDirectoryServiceAsync to GoogleWorkspaceSyncService**

`GoogleWorkspaceSyncService` has `GetCloudIdentityServiceAsync()` and `GetGroupssettingsServiceAsync()` but no Directory API factory. Add one following the same pattern. In `GoogleWorkspaceSyncService.cs`, after the existing `GetGroupssettingsServiceAsync()` method (around line 110), add:

```csharp
private DirectoryService? _directoryService;

private async Task<DirectoryService> GetDirectoryServiceAsync()
{
    if (_directoryService is not null)
        return _directoryService;

    var credential = await GetCredentialAsync();

    _directoryService = new DirectoryService(new BaseClientService.Initializer
    {
        HttpClientInitializer = credential,
        ApplicationName = "Humans"
    });

    return _directoryService;
}
```

You'll also need the using: `using Google.Apis.Admin.Directory.directory_v1;` (check if already present from existing code). The `GetCredentialAsync()` method already exists in `GoogleWorkspaceSyncService` — the credential is shared across all Google APIs.

Also add `GoogleResourceSettingsRemediated` to `AuditAction.cs` (needed by Task 9):

```csharp
// In src/Humans.Domain/Enums/AuditAction.cs, add before the closing }:
GoogleResourceSettingsRemediated,
```

- [ ] **Step 1: Add DTO for backfill results**

Create in `src/Humans.Application/DTOs/EmailBackfillResult.cs`:

```csharp
namespace Humans.Application.DTOs;

public class EmailMismatch
{
    public Guid UserId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string StoredEmail { get; init; } = string.Empty;
    public string GoogleEmail { get; init; } = string.Empty;
}

public class EmailBackfillResult
{
    public List<EmailMismatch> Mismatches { get; init; } = [];
    public int TotalUsersChecked { get; init; }
    public string? ErrorMessage { get; init; }
}
```

- [ ] **Step 2: Add interface methods**

In `IGoogleSyncService.cs`, add:

```csharp
/// <summary>
/// Queries Google Admin SDK for all domain users and compares against stored emails.
/// Returns mismatches for review — does NOT modify any data.
/// </summary>
Task<EmailBackfillResult> GetEmailMismatchesAsync(CancellationToken cancellationToken = default);
```

- [ ] **Step 3: Implement in GoogleWorkspaceSyncService**

Add to `GoogleWorkspaceSyncService.cs`:

```csharp
/// <inheritdoc />
public async Task<EmailBackfillResult> GetEmailMismatchesAsync(CancellationToken cancellationToken = default)
{
    try
    {
        var directory = await GetDirectoryServiceAsync();
        var mismatches = new List<EmailMismatch>();
        var totalChecked = 0;

        // Get all domain users from Admin SDK
        string? pageToken = null;
        do
        {
            var request = directory.Users.List();
            request.Domain = _settings.Domain;
            request.MaxResults = 500;
            request.Projection = Google.Apis.Admin.Directory.directory_v1.UsersResource.ListRequest.ProjectionEnum.Basic;
            if (pageToken is not null)
                request.PageToken = pageToken;

            var response = await request.ExecuteAsync(cancellationToken);

            if (response.UsersValue is not null)
            {
                foreach (var googleUser in response.UsersValue)
                {
                    totalChecked++;
                    var googleEmail = googleUser.PrimaryEmail;
                    if (string.IsNullOrEmpty(googleEmail)) continue;

                    // Find matching user in our DB by normalized comparison
                    var storedUser = await _dbContext.Users
                        .Where(u => u.Email != null)
                        .FirstOrDefaultAsync(u => u.Email!.ToLower() == googleEmail.ToLower()
                            || u.Email!.ToLower() == EmailNormalization.NormalizeForComparison(googleEmail), cancellationToken);

                    if (storedUser?.Email is not null &&
                        !string.Equals(storedUser.Email, googleEmail, StringComparison.OrdinalIgnoreCase))
                    {
                        mismatches.Add(new EmailMismatch
                        {
                            UserId = storedUser.Id,
                            DisplayName = storedUser.DisplayName,
                            StoredEmail = storedUser.Email,
                            GoogleEmail = googleEmail
                        });
                    }
                }
            }

            pageToken = response.NextPageToken;
        } while (!string.IsNullOrEmpty(pageToken));

        return new EmailBackfillResult
        {
            Mismatches = mismatches,
            TotalUsersChecked = totalChecked
        };
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Failed to check email mismatches against Admin SDK");
        return new EmailBackfillResult
        {
            ErrorMessage = $"Failed to query Google Admin SDK: {ex.Message}"
        };
    }
}
```

- [ ] **Step 4: Add stub implementation**

In `StubGoogleSyncService.cs`, add:

```csharp
public Task<EmailBackfillResult> GetEmailMismatchesAsync(CancellationToken cancellationToken = default)
{
    _logger.LogInformation("[STUB] Would check email mismatches against Admin SDK");
    return Task.FromResult(new EmailBackfillResult());
}
```

- [ ] **Step 5: Add admin controller actions**

In `AdminController.cs`, before the closing `}`, add:

```csharp
[HttpPost("CheckEmailMismatches")]
[ValidateAntiForgeryToken]
public async Task<IActionResult> CheckEmailMismatches(
    [FromServices] IGoogleSyncService googleSyncService)
{
    try
    {
        var result = await googleSyncService.GetEmailMismatchesAsync();
        TempData["EmailBackfillResult"] = System.Text.Json.JsonSerializer.Serialize(result);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Failed to check email mismatches");
        SetError($"Email mismatch check failed: {ex.Message}");
        return RedirectToAction(nameof(Index));
    }

    return RedirectToAction(nameof(EmailBackfillReview));
}

[HttpGet("EmailBackfillReview")]
public IActionResult EmailBackfillReview()
{
    Application.DTOs.EmailBackfillResult? result = null;
    if (TempData["EmailBackfillResult"] is string json)
    {
        result = System.Text.Json.JsonSerializer.Deserialize<Application.DTOs.EmailBackfillResult>(json);
    }

    if (result is null)
    {
        SetInfo("No email mismatch results to display. Run the check first.");
        return RedirectToAction(nameof(Index));
    }

    return View(result);
}

[HttpPost("ApplyEmailBackfill")]
[ValidateAntiForgeryToken]
public async Task<IActionResult> ApplyEmailBackfill(
    [FromForm] List<Guid> selectedUserIds,
    [FromForm] Dictionary<string, string> corrections)
{
    var applied = 0;
    foreach (var userId in selectedUserIds)
    {
        if (!corrections.TryGetValue(userId.ToString(), out var newEmail)) continue;

        var user = await _dbContext.Users.FindAsync(userId);
        if (user is null) continue;

        var oldEmail = user.Email;
        user.Email = newEmail;
        user.NormalizedEmail = newEmail.ToUpperInvariant();
        user.UserName = newEmail;
        user.NormalizedUserName = newEmail.ToUpperInvariant();

        // Also update the UserEmails table
        var userEmail = await _dbContext.Set<Humans.Domain.Entities.UserEmail>()
            .FirstOrDefaultAsync(e => e.UserId == userId && e.IsOAuth);
        if (userEmail is not null)
        {
            userEmail.Email = newEmail;
        }

        _logger.LogInformation("Admin corrected email for user {UserId}: {OldEmail} → {NewEmail}",
            userId, oldEmail, newEmail);
        applied++;
    }

    if (applied > 0)
    {
        await _dbContext.SaveChangesAsync();
    }

    SetSuccess($"Updated {applied} email address(es).");
    return RedirectToAction(nameof(Index));
}
```

- [ ] **Step 6: Create the review view**

Create `src/Humans.Web/Views/Admin/EmailBackfillReview.cshtml`:

```html
@model Humans.Application.DTOs.EmailBackfillResult
@{
    ViewData["Title"] = "Email Backfill Review";
}

<div class="container-fluid px-4">
    <div class="d-flex justify-content-between align-items-center mt-4 mb-3">
        <h1>Email Backfill Review</h1>
        <a asp-action="Index" class="btn btn-outline-secondary btn-sm">
            <i class="fa-solid fa-arrow-left me-1"></i>Back to Admin
        </a>
    </div>

    @if (Model.ErrorMessage is not null)
    {
        <div class="alert alert-danger">
            <i class="fa-solid fa-triangle-exclamation me-1"></i>@Model.ErrorMessage
        </div>
    }
    else if (Model.Mismatches.Count == 0)
    {
        <div class="alert alert-success">
            <i class="fa-solid fa-check-circle me-1"></i>No email mismatches found across @Model.TotalUsersChecked Google users.
        </div>
    }
    else
    {
        <div class="alert alert-warning">
            <i class="fa-solid fa-magnifying-glass me-1"></i>Found @Model.Mismatches.Count mismatch(es) across @Model.TotalUsersChecked Google users.
            Review and select which to correct.
        </div>

        <form asp-action="ApplyEmailBackfill" method="post">
            <table class="table table-sm table-striped">
                <thead>
                    <tr>
                        <th><input type="checkbox" id="selectAll" /></th>
                        <th>Human</th>
                        <th>Stored Email</th>
                        <th>Google Email</th>
                    </tr>
                </thead>
                <tbody>
                    @foreach (var m in Model.Mismatches)
                    {
                        <tr>
                            <td>
                                <input type="checkbox" name="selectedUserIds" value="@m.UserId" class="backfill-check" />
                                <input type="hidden" name="corrections[@m.UserId]" value="@m.GoogleEmail" />
                            </td>
                            <td>@m.DisplayName</td>
                            <td><code class="text-danger">@m.StoredEmail</code></td>
                            <td><code class="text-success">@m.GoogleEmail</code></td>
                        </tr>
                    }
                </tbody>
            </table>

            <button type="submit" class="btn btn-warning">
                <i class="fa-solid fa-check me-1"></i>Apply Selected Corrections
            </button>
        </form>

        <script>
            document.getElementById('selectAll')?.addEventListener('change', function() {
                document.querySelectorAll('.backfill-check').forEach(cb => cb.checked = this.checked);
            });
        </script>
    }
</div>
```

- [ ] **Step 7: Verify it compiles**

Run: `dotnet build Humans.slnx`
Expected: Build succeeded.

- [ ] **Step 8: Commit**

```bash
git add src/Humans.Application/DTOs/EmailBackfillResult.cs src/Humans.Application/Interfaces/IGoogleSyncService.cs src/Humans.Infrastructure/Services/GoogleWorkspaceSyncService.cs src/Humans.Infrastructure/Services/StubGoogleSyncService.cs src/Humans.Web/Controllers/AdminController.cs src/Humans.Web/Views/Admin/EmailBackfillReview.cshtml
git commit -m "feat: add email backfill review page for googlemail corrections (#193)

Admin can query Google Admin SDK for all domain users, see which stored
emails differ from Google's records, review the diff table, and apply
selected corrections. No blanket replacements — human review required."
```

### Task 7: Add GroupLinkResult DTO and validate in EnsureTeamGroupAsync (#173)

**Files:**
- Create: `src/Humans.Application/DTOs/GroupLinkResult.cs`
- Modify: `src/Humans.Application/Interfaces/IGoogleSyncService.cs:70`
- Modify: `src/Humans.Infrastructure/Services/GoogleWorkspaceSyncService.cs:1135-1249`
- Modify: `src/Humans.Infrastructure/Services/StubGoogleSyncService.cs:109-113`

- [ ] **Step 1: Create GroupLinkResult DTO**

Create `src/Humans.Application/DTOs/GroupLinkResult.cs`:

```csharp
namespace Humans.Application.DTOs;

public class GroupLinkResult
{
    public bool Success { get; init; }
    public string? WarningMessage { get; init; }
    public string? ErrorMessage { get; init; }
    public bool RequiresConfirmation { get; init; }
    public Guid? InactiveResourceId { get; init; }

    public static GroupLinkResult Ok() => new() { Success = true };
    public static GroupLinkResult Error(string message) => new() { ErrorMessage = message };
    public static GroupLinkResult NeedsConfirmation(string message, Guid inactiveResourceId) =>
        new() { RequiresConfirmation = true, WarningMessage = message, InactiveResourceId = inactiveResourceId };
}
```

- [ ] **Step 2: Update interface signature**

In `IGoogleSyncService.cs`, change line 70:
```csharp
// Before:
Task EnsureTeamGroupAsync(Guid teamId, CancellationToken cancellationToken = default);
```
```csharp
// After:
Task<GroupLinkResult> EnsureTeamGroupAsync(Guid teamId, bool confirmReactivation = false, CancellationToken cancellationToken = default);
```

- [ ] **Step 3: Update StubGoogleSyncService**

In `StubGoogleSyncService.cs`, change lines 109-113:
```csharp
// Before:
public Task EnsureTeamGroupAsync(Guid teamId, CancellationToken cancellationToken = default)
{
    _logger.LogInformation("[STUB] Would ensure Google Group exists for team {TeamId}", teamId);
    return Task.CompletedTask;
}
```
```csharp
// After:
public Task<GroupLinkResult> EnsureTeamGroupAsync(Guid teamId, bool confirmReactivation = false, CancellationToken cancellationToken = default)
{
    _logger.LogInformation("[STUB] Would ensure Google Group exists for team {TeamId}", teamId);
    return Task.FromResult(GroupLinkResult.Ok());
}
```

- [ ] **Step 4: Update GoogleWorkspaceSyncService.EnsureTeamGroupAsync**

Change the method signature at line 1135:
```csharp
// Before:
public async Task EnsureTeamGroupAsync(Guid teamId, CancellationToken cancellationToken = default)
```
```csharp
// After:
public async Task<GroupLinkResult> EnsureTeamGroupAsync(Guid teamId, bool confirmReactivation = false, CancellationToken cancellationToken = default)
```

Update all early `return;` statements to `return GroupLinkResult.Ok();`.

**Add validation before the Cloud Identity lookup** (before line 1205), after the `email` variable is set:

```csharp
// Check for existing active GoogleResource with this GoogleId (any team)
var existingActiveByEmail = await _dbContext.GoogleResources
    .Include(r => r.Team)
    .Where(r => r.IsActive && r.ResourceType == GoogleResourceType.Group)
    .Where(r => r.Url != null && r.Url.EndsWith($"/g/{team.GoogleGroupPrefix}"))
    .FirstOrDefaultAsync(cancellationToken);

if (existingActiveByEmail is not null)
{
    if (existingActiveByEmail.TeamId == teamId)
    {
        return GroupLinkResult.Error("This group is already linked to this team.");
    }
    else
    {
        return GroupLinkResult.Error($"This group is already linked to team \"{existingActiveByEmail.Team.Name}\".");
    }
}

// Check for inactive resource for this team (reactivation scenario)
var inactiveForTeam = await _dbContext.GoogleResources
    .Where(r => !r.IsActive && r.ResourceType == GoogleResourceType.Group && r.TeamId == teamId)
    .Where(r => r.Url != null && r.Url.EndsWith($"/g/{team.GoogleGroupPrefix}"))
    .FirstOrDefaultAsync(cancellationToken);

if (inactiveForTeam is not null && !confirmReactivation)
{
    return GroupLinkResult.NeedsConfirmation(
        "This group was previously linked to this team. Reactivate it?",
        inactiveForTeam.Id);
}

if (inactiveForTeam is not null && confirmReactivation)
{
    inactiveForTeam.IsActive = true;
    inactiveForTeam.LastSyncedAt = _clock.GetCurrentInstant();
    await _dbContext.SaveChangesAsync(cancellationToken);

    _logger.LogInformation("Reactivated Google Group resource {ResourceId} for team {TeamId}",
        inactiveForTeam.Id, teamId);

    await _auditLogService.LogAsync(
        AuditAction.GoogleResourceProvisioned, nameof(GoogleResource), inactiveForTeam.Id,
        $"Reactivated Google Group resource for team",
        nameof(GoogleWorkspaceSyncService),
        relatedEntityId: teamId, relatedEntityType: nameof(Team));

    return GroupLinkResult.Ok();
}
```

- [ ] **Step 5: Update TeamController to handle GroupLinkResult**

In `TeamController.cs`, change the EnsureTeamGroupAsync call block (lines ~680-690):

```csharp
// Before:
try
{
    await _googleSyncService.EnsureTeamGroupAsync(id);
}
catch (Exception ex)
{
    _logger.LogError(ex, "Failed to sync Google Group for team {TeamId}", id);
    SetSuccess(_localizer["Admin_TeamUpdated"].Value);
    SetError($"Team updated but Google Group setup failed: {ex.Message}");
    return RedirectToAction(nameof(Summary));
}
```

```csharp
// After:
try
{
    var groupResult = await _googleSyncService.EnsureTeamGroupAsync(id);
    if (!groupResult.Success)
    {
        if (groupResult.RequiresConfirmation)
        {
            SetSuccess(_localizer["Admin_TeamUpdated"].Value);
            SetError(groupResult.WarningMessage ?? "Confirmation required for group reactivation.");
            // TODO: Add confirmation flow in a follow-up if needed
            return RedirectToAction(nameof(Summary));
        }

        SetSuccess(_localizer["Admin_TeamUpdated"].Value);
        SetError(groupResult.ErrorMessage ?? "Google Group linking failed.");
        return RedirectToAction(nameof(Summary));
    }
}
catch (Exception ex)
{
    _logger.LogError(ex, "Failed to sync Google Group for team {TeamId}", id);
    SetSuccess(_localizer["Admin_TeamUpdated"].Value);
    SetError($"Team updated but Google Group setup failed: {ex.Message}");
    return RedirectToAction(nameof(Summary));
}
```

- [ ] **Step 6: Verify it compiles**

Run: `dotnet build Humans.slnx`
Expected: Build succeeded.

- [ ] **Step 7: Commit**

```bash
git add src/Humans.Application/DTOs/GroupLinkResult.cs src/Humans.Application/Interfaces/IGoogleSyncService.cs src/Humans.Infrastructure/Services/GoogleWorkspaceSyncService.cs src/Humans.Infrastructure/Services/StubGoogleSyncService.cs src/Humans.Web/Controllers/TeamController.cs
git commit -m "fix: validate before linking Google Group to team (#173)

Check for existing active resources before creating — prevents duplicate
key errors. Blocks same-group-different-team sharing. Offers reactivation
for previously linked inactive groups. Returns GroupLinkResult instead of
void so controller can show user-friendly validation messages."
```

### Task 8: Correct WhoCanViewMembership default (#195 Part 4)

**Files:**
- Modify: `src/Humans.Infrastructure/Configuration/GoogleWorkspaceSettings.cs:58`
- Modify: `src/Humans.Web/appsettings.json:34`

- [ ] **Step 1: Update the settings class default**

In `GoogleWorkspaceSettings.cs` line 58, change:
```csharp
public string WhoCanViewMembership { get; set; } = "ALL_MEMBERS_CAN_VIEW";
```
to:
```csharp
public string WhoCanViewMembership { get; set; } = "OWNERS_AND_MANAGERS";
```

- [ ] **Step 2: Update appsettings.json**

In `appsettings.json` line 34, change:
```json
"WhoCanViewMembership": "ALL_MEMBERS_CAN_VIEW",
```
to:
```json
"WhoCanViewMembership": "OWNERS_AND_MANAGERS",
```

- [ ] **Step 3: Verify it compiles**

Run: `dotnet build Humans.slnx`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Infrastructure/Configuration/GoogleWorkspaceSettings.cs src/Humans.Web/appsettings.json
git commit -m "fix: change WhoCanViewMembership default to OWNERS_AND_MANAGERS (#195)

Members should not see group membership lists — restrict to owners and
managers only. Updated in settings class default and appsettings.json."
```

### Task 9: Add RemediateGroupSettingsAsync (#195 Part 1)

**Files:**
- Modify: `src/Humans.Application/Interfaces/IGoogleSyncService.cs`
- Modify: `src/Humans.Infrastructure/Services/GoogleWorkspaceSyncService.cs`
- Modify: `src/Humans.Infrastructure/Services/StubGoogleSyncService.cs`

- [ ] **Step 1: Add interface method**

In `IGoogleSyncService.cs`, add:

```csharp
/// <summary>
/// Applies expected settings to a Google Group, fixing any drift.
/// Respects SyncSettings mode — returns without action if sync is disabled.
/// </summary>
Task<bool> RemediateGroupSettingsAsync(string groupEmail, CancellationToken cancellationToken = default);
```

- [ ] **Step 2: Implement in GoogleWorkspaceSyncService**

Add to `GoogleWorkspaceSyncService.cs`:

```csharp
/// <inheritdoc />
public async Task<bool> RemediateGroupSettingsAsync(string groupEmail, CancellationToken cancellationToken = default)
{
    var mode = await _syncSettingsService.GetModeAsync(SyncServiceType.GoogleGroups, cancellationToken);
    if (mode == SyncMode.None)
    {
        _logger.LogWarning("Google Groups sync is disabled — cannot remediate settings for {GroupEmail}", groupEmail);
        return false;
    }

    try
    {
        var groupssettingsService = await GetGroupssettingsServiceAsync();
        var expected = BuildExpectedSettingsDictionary();

        var settings = new Google.Apis.Groupssettings.v1.Data.Groups
        {
            WhoCanJoin = expected["WhoCanJoin"],
            WhoCanViewMembership = expected["WhoCanViewMembership"],
            WhoCanContactOwner = expected["WhoCanContactOwner"],
            WhoCanPostMessage = expected["WhoCanPostMessage"],
            WhoCanViewGroup = expected["WhoCanViewGroup"],
            WhoCanModerateMembers = expected["WhoCanModerateMembers"],
            AllowExternalMembers = expected["AllowExternalMembers"],
            IsArchived = expected["IsArchived"],
            MembersCanPostAsTheGroup = expected["MembersCanPostAsTheGroup"],
            IncludeInGlobalAddressList = expected["IncludeInGlobalAddressList"],
            AllowWebPosting = expected["AllowWebPosting"],
            MessageModerationLevel = expected["MessageModerationLevel"],
            SpamModerationLevel = expected["SpamModerationLevel"],
            EnableCollaborativeInbox = expected["EnableCollaborativeInbox"]
        };

        var request = groupssettingsService.Groups.Update(settings, groupEmail);
        await request.ExecuteAsync(cancellationToken);

        _logger.LogInformation("Remediated settings for Google Group {GroupEmail}", groupEmail);

        await _auditLogService.LogAsync(
            AuditAction.GoogleResourceSettingsRemediated, nameof(GoogleResource), Guid.Empty,
            $"Remediated settings for Google Group '{groupEmail}'",
            nameof(GoogleWorkspaceSyncService));

        return true;
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Failed to remediate settings for Google Group {GroupEmail}", groupEmail);
        throw;
    }
}
```

- [ ] **Step 3: Add stub implementation**

In `StubGoogleSyncService.cs`, add:

```csharp
public Task<bool> RemediateGroupSettingsAsync(string groupEmail, CancellationToken cancellationToken = default)
{
    _logger.LogInformation("[STUB] Would remediate settings for Google Group {GroupEmail}", groupEmail);
    return Task.FromResult(true);
}
```

- [ ] **Step 4: Verify it compiles**

Run: `dotnet build Humans.slnx`
Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Application/Interfaces/IGoogleSyncService.cs src/Humans.Infrastructure/Services/GoogleWorkspaceSyncService.cs src/Humans.Infrastructure/Services/StubGoogleSyncService.cs
git commit -m "feat: add RemediateGroupSettingsAsync to fix drifted settings (#195)

Applies all 14 expected settings to a Google Group via Groups Settings
API. Respects SyncSettings mode and logs via audit trail."
```

### Task 10: Add domain-wide All Groups page (#195 Part 2)

**Files:**
- Modify: `src/Humans.Application/Interfaces/IGoogleSyncService.cs`
- Modify: `src/Humans.Infrastructure/Services/GoogleWorkspaceSyncService.cs`
- Modify: `src/Humans.Infrastructure/Services/StubGoogleSyncService.cs`
- Create: `src/Humans.Application/DTOs/DomainGroupInfo.cs`
- Modify: `src/Humans.Web/Controllers/AdminController.cs`
- Create: `src/Humans.Web/Views/Admin/AllGroups.cshtml`

- [ ] **Step 1: Create DTO**

Create `src/Humans.Application/DTOs/DomainGroupInfo.cs`:

```csharp
namespace Humans.Application.DTOs;

public class DomainGroupInfo
{
    public string GroupEmail { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? GoogleId { get; init; }
    public int MemberCount { get; init; }
    public string? LinkedTeamName { get; init; }
    public Guid? LinkedTeamId { get; init; }
    public Dictionary<string, string> ActualSettings { get; init; } = [];
    public List<GroupSettingDrift> Drifts { get; init; } = [];
    public bool HasDrift => Drifts.Count > 0;
    public string? ErrorMessage { get; init; }
}

public class AllGroupsResult
{
    public List<DomainGroupInfo> Groups { get; init; } = [];
    public Dictionary<string, string> ExpectedSettings { get; init; } = [];
    public string? ErrorMessage { get; init; }
}
```

- [ ] **Step 2: Add interface method**

In `IGoogleSyncService.cs`, add:

```csharp
/// <summary>
/// Queries all Google Groups on the domain via Admin SDK, cross-references with
/// linked team resources, and checks settings for each group.
/// </summary>
Task<AllGroupsResult> GetAllDomainGroupsAsync(CancellationToken cancellationToken = default);
```

- [ ] **Step 3: Implement in GoogleWorkspaceSyncService**

Add to `GoogleWorkspaceSyncService.cs`:

```csharp
/// <inheritdoc />
public async Task<AllGroupsResult> GetAllDomainGroupsAsync(CancellationToken cancellationToken = default)
{
    try
    {
        var directory = await GetDirectoryServiceAsync();
        var expectedSettings = BuildExpectedSettingsDictionary();

        // Get all team-linked group resources for cross-reference
        var linkedResources = await _dbContext.GoogleResources
            .Include(r => r.Team)
            .Where(r => r.ResourceType == GoogleResourceType.Group && r.IsActive)
            .ToListAsync(cancellationToken);

        var linkedByUrl = linkedResources
            .Where(r => r.Url is not null)
            .ToDictionary(r => r.Url!, r => r, StringComparer.OrdinalIgnoreCase);

        // Enumerate all groups on the domain
        var groups = new List<DomainGroupInfo>();
        string? pageToken = null;
        do
        {
            var request = directory.Groups.List();
            request.Domain = _settings.Domain;
            request.MaxResults = 200;
            if (pageToken is not null)
                request.PageToken = pageToken;

            var response = await request.ExecuteAsync(cancellationToken);

            if (response.GroupsValue is not null)
            {
                foreach (var group in response.GroupsValue)
                {
                    var groupUrl = $"https://groups.google.com/a/{_settings.Domain}/g/{group.Email?.Split('@')[0]}";

                    linkedByUrl.TryGetValue(groupUrl, out var linked);

                    // Get settings for this group
                    var settingsDict = new Dictionary<string, string>();
                    var drifts = new List<GroupSettingDrift>();
                    string? errorMessage = null;

                    try
                    {
                        var groupssettingsService = await GetGroupssettingsServiceAsync();
                        var settingsRequest = groupssettingsService.Groups.Get(group.Email);
                        settingsRequest.Alt = Google.Apis.Groupssettings.v1.GroupssettingsBaseServiceRequest<Google.Apis.Groupssettings.v1.Data.Groups>.AltEnum.Json;
                        var actual = await settingsRequest.ExecuteAsync(cancellationToken);

                        settingsDict = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["WhoCanJoin"] = actual.WhoCanJoin ?? "",
                            ["WhoCanViewMembership"] = actual.WhoCanViewMembership ?? "",
                            ["WhoCanContactOwner"] = actual.WhoCanContactOwner ?? "",
                            ["WhoCanPostMessage"] = actual.WhoCanPostMessage ?? "",
                            ["WhoCanViewGroup"] = actual.WhoCanViewGroup ?? "",
                            ["WhoCanModerateMembers"] = actual.WhoCanModerateMembers ?? "",
                            ["AllowExternalMembers"] = actual.AllowExternalMembers ?? "",
                            ["IsArchived"] = actual.IsArchived ?? "",
                            ["MembersCanPostAsTheGroup"] = actual.MembersCanPostAsTheGroup ?? "",
                            ["IncludeInGlobalAddressList"] = actual.IncludeInGlobalAddressList ?? "",
                            ["AllowWebPosting"] = actual.AllowWebPosting ?? "",
                            ["MessageModerationLevel"] = actual.MessageModerationLevel ?? "",
                            ["SpamModerationLevel"] = actual.SpamModerationLevel ?? "",
                            ["EnableCollaborativeInbox"] = actual.EnableCollaborativeInbox ?? ""
                        };

                        foreach (var (key, expectedValue) in expectedSettings)
                        {
                            if (settingsDict.TryGetValue(key, out var actualValue) &&
                                !string.Equals(expectedValue, actualValue, StringComparison.OrdinalIgnoreCase))
                            {
                                drifts.Add(new GroupSettingDrift(key, expectedValue, actualValue));
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        errorMessage = $"Cannot read settings: {ex.Message}";
                    }

                    groups.Add(new DomainGroupInfo
                    {
                        GroupEmail = group.Email ?? "",
                        DisplayName = group.Name ?? "",
                        GoogleId = group.Id,
                        MemberCount = (int)(group.DirectMembersCount ?? 0),
                        LinkedTeamName = linked?.Team.Name,
                        LinkedTeamId = linked?.TeamId,
                        ActualSettings = settingsDict,
                        Drifts = drifts,
                        ErrorMessage = errorMessage
                    });
                }
            }

            pageToken = response.NextPageToken;
        } while (!string.IsNullOrEmpty(pageToken));

        return new AllGroupsResult
        {
            Groups = groups.OrderBy(g => g.LinkedTeamName is null).ThenBy(g => g.GroupEmail).ToList(),
            ExpectedSettings = expectedSettings
        };
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Failed to enumerate domain groups");
        return new AllGroupsResult { ErrorMessage = $"Failed to query domain groups: {ex.Message}" };
    }
}
```

- [ ] **Step 4: Add stub**

In `StubGoogleSyncService.cs`, add:

```csharp
public Task<AllGroupsResult> GetAllDomainGroupsAsync(CancellationToken cancellationToken = default)
{
    _logger.LogInformation("[STUB] Would enumerate all domain groups");
    return Task.FromResult(new AllGroupsResult());
}
```

- [ ] **Step 5: Add controller actions**

In `AdminController.cs`, add:

```csharp
[HttpGet("AllGroups")]
public async Task<IActionResult> AllGroups(
    [FromServices] IGoogleSyncService googleSyncService)
{
    try
    {
        var result = await googleSyncService.GetAllDomainGroupsAsync();
        return View(result);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Failed to load domain groups");
        SetError($"Failed to load domain groups: {ex.Message}");
        return RedirectToAction(nameof(Index));
    }
}

[HttpPost("RemediateGroupSettings")]
[ValidateAntiForgeryToken]
public async Task<IActionResult> RemediateGroupSettings(
    [FromServices] IGoogleSyncService googleSyncService,
    [FromForm] string groupEmail,
    [FromForm] string? returnUrl)
{
    try
    {
        var success = await googleSyncService.RemediateGroupSettingsAsync(groupEmail);
        if (success)
            SetSuccess($"Settings remediated for {groupEmail}.");
        else
            SetError($"Remediation skipped for {groupEmail} — sync may be disabled.");
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Failed to remediate settings for {GroupEmail}", groupEmail);
        SetError($"Remediation failed for {groupEmail}: {ex.Message}");
    }

    return Redirect(returnUrl ?? Url.Action(nameof(AllGroups))!);
}

[HttpPost("RemediateAllGroupSettings")]
[ValidateAntiForgeryToken]
public async Task<IActionResult> RemediateAllGroupSettings(
    [FromServices] IGoogleSyncService googleSyncService,
    [FromForm] List<string> groupEmails)
{
    var success = 0;
    var failed = 0;
    foreach (var email in groupEmails)
    {
        try
        {
            if (await googleSyncService.RemediateGroupSettingsAsync(email))
                success++;
            else
                failed++;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remediate {GroupEmail}", email);
            failed++;
        }
    }

    SetSuccess($"Remediated {success} group(s). {(failed > 0 ? $"{failed} failed." : "")}");
    return RedirectToAction(nameof(AllGroups));
}

[HttpPost("LinkGroupToTeam")]
[ValidateAntiForgeryToken]
public async Task<IActionResult> LinkGroupToTeam(
    [FromServices] IGoogleSyncService googleSyncService,
    [FromForm] Guid teamId,
    [FromForm] string groupPrefix)
{
    try
    {
        // Set the prefix on the team, then let EnsureTeamGroupAsync link it
        var team = await _dbContext.Teams.FindAsync(teamId);
        if (team is null)
        {
            SetError("Team not found.");
            return RedirectToAction(nameof(AllGroups));
        }

        team.GoogleGroupPrefix = groupPrefix;
        await _dbContext.SaveChangesAsync();

        var result = await googleSyncService.EnsureTeamGroupAsync(teamId);
        if (result.Success)
            SetSuccess($"Group linked to team \"{team.Name}\".");
        else
            SetError(result.ErrorMessage ?? result.WarningMessage ?? "Linking failed.");
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Failed to link group to team {TeamId}", teamId);
        SetError($"Linking failed: {ex.Message}");
    }

    return RedirectToAction(nameof(AllGroups));
}
```

- [ ] **Step 6: Create the All Groups view**

Create `src/Humans.Web/Views/Admin/AllGroups.cshtml`:

```html
@model Humans.Application.DTOs.AllGroupsResult
@{
    ViewData["Title"] = "All Domain Groups";
    var teams = ViewBag.Teams as List<Humans.Domain.Entities.Team> ?? new();
}

<div class="container-fluid px-4">
    <div class="d-flex justify-content-between align-items-center mt-4 mb-3">
        <h1>All Domain Groups</h1>
        <div>
            <a asp-action="GroupSettingsResults" class="btn btn-outline-secondary btn-sm me-1">
                <i class="fa-solid fa-filter me-1"></i>Team-linked only
            </a>
            <a asp-action="Index" class="btn btn-outline-secondary btn-sm">
                <i class="fa-solid fa-arrow-left me-1"></i>Back to Admin
            </a>
        </div>
    </div>

    @if (Model.ErrorMessage is not null)
    {
        <div class="alert alert-danger">
            <i class="fa-solid fa-triangle-exclamation me-1"></i>@Model.ErrorMessage
        </div>
    }
    else
    {
        var driftCount = Model.Groups.Count(g => g.HasDrift);
        var unlinkedCount = Model.Groups.Count(g => g.LinkedTeamName is null);
        <div class="alert alert-info">
            <i class="fa-solid fa-globe me-1"></i>@Model.Groups.Count group(s) on domain.
            @if (driftCount > 0) { <strong>@driftCount with settings drift.</strong> }
            @if (unlinkedCount > 0) { <strong>@unlinkedCount not linked to a team.</strong> }
        </div>

        @if (driftCount > 0)
        {
            <form asp-action="RemediateAllGroupSettings" method="post" class="mb-3">
                @foreach (var g in Model.Groups.Where(g => g.HasDrift))
                {
                    <input type="hidden" name="groupEmails" value="@g.GroupEmail" />
                }
                <button type="submit" class="btn btn-warning btn-sm">
                    <i class="fa-solid fa-wrench me-1"></i>Fix All @driftCount Drifted Group(s)
                </button>
            </form>
        }

        <div class="table-responsive">
            <table class="table table-sm table-striped align-middle">
                <thead class="table-dark">
                    <tr>
                        <th>Group Email</th>
                        <th>Display Name</th>
                        <th>Team</th>
                        <th class="text-center">Members</th>
                        <th class="text-center">Drift</th>
                        <th>Actions</th>
                    </tr>
                </thead>
                <tbody>
                    @foreach (var group in Model.Groups)
                    {
                        var rowClass = group.LinkedTeamName is null ? "table-light" : "";
                        <tr class="@rowClass">
                            <td><code>@group.GroupEmail</code></td>
                            <td>@group.DisplayName</td>
                            <td>
                                @if (group.LinkedTeamName is not null)
                                {
                                    <span class="badge bg-primary">@group.LinkedTeamName</span>
                                }
                                else
                                {
                                    <span class="badge bg-secondary">Unlinked</span>
                                }
                            </td>
                            <td class="text-center">@group.MemberCount</td>
                            <td class="text-center">
                                @if (group.ErrorMessage is not null)
                                {
                                    <span class="badge bg-danger" title="@group.ErrorMessage">Error</span>
                                }
                                else if (group.HasDrift)
                                {
                                    <span class="badge bg-warning text-dark" title="@string.Join(", ", group.Drifts.Select(d => $"{d.SettingName}: {d.ActualValue} → {d.ExpectedValue}"))">
                                        @group.Drifts.Count drift(s)
                                    </span>
                                }
                                else
                                {
                                    <span class="badge bg-success">OK</span>
                                }
                            </td>
                            <td>
                                @if (group.HasDrift)
                                {
                                    <form asp-action="RemediateGroupSettings" method="post" class="d-inline">
                                        <input type="hidden" name="groupEmail" value="@group.GroupEmail" />
                                        <input type="hidden" name="returnUrl" value="@Url.Action("AllGroups")" />
                                        <button type="submit" class="btn btn-warning btn-sm">Fix</button>
                                    </form>
                                }
                                @if (group.LinkedTeamName is null)
                                {
                                    <button type="button" class="btn btn-outline-primary btn-sm"
                                            data-bs-toggle="modal" data-bs-target="#linkModal-@group.GoogleId">
                                        Link to Team
                                    </button>
                                }
                            </td>
                        </tr>
                    }
                </tbody>
            </table>
        </div>

        @* Link-to-Team modals for unlinked groups *@
        @foreach (var group in Model.Groups.Where(g => g.LinkedTeamName is null))
        {
            <div class="modal fade" id="linkModal-@group.GoogleId" tabindex="-1">
                <div class="modal-dialog">
                    <div class="modal-content">
                        <form asp-action="LinkGroupToTeam" method="post">
                            <div class="modal-header">
                                <h5 class="modal-title">Link @group.GroupEmail to a Team</h5>
                                <button type="button" class="btn-close" data-bs-dismiss="modal"></button>
                            </div>
                            <div class="modal-body">
                                <input type="hidden" name="groupPrefix" value="@group.GroupEmail.Split('@')[0]" />
                                <div class="mb-3">
                                    <label class="form-label">Select Team</label>
                                    <select name="teamId" class="form-select" required>
                                        <option value="">Choose...</option>
                                        @foreach (var team in teams)
                                        {
                                            <option value="@team.Id">@team.Name</option>
                                        }
                                    </select>
                                </div>
                            </div>
                            <div class="modal-footer">
                                <button type="button" class="btn btn-secondary" data-bs-dismiss="modal">Cancel</button>
                                <button type="submit" class="btn btn-primary">Link</button>
                            </div>
                        </form>
                    </div>
                </div>
            </div>
        }

        @* Expected settings reference *@
        <div class="card mt-4 mb-4">
            <div class="card-header bg-light">
                <i class="fa-solid fa-list-check me-1"></i><strong>Expected Settings Reference (all 14)</strong>
            </div>
            <div class="card-body p-0">
                <table class="table table-sm table-striped mb-0">
                    <thead>
                        <tr>
                            <th>Setting</th>
                            <th>Expected Value</th>
                        </tr>
                    </thead>
                    <tbody>
                        @foreach (var setting in Model.ExpectedSettings)
                        {
                            <tr>
                                <td><code>@setting.Key</code></td>
                                <td><span class="badge bg-success">@setting.Value</span></td>
                            </tr>
                        }
                    </tbody>
                </table>
            </div>
        </div>
    }
</div>
```

- [ ] **Step 7: Pass teams list to the view via ViewBag**

In the `AllGroups` action in `AdminController.cs`, before `return View(result)`, add:

```csharp
ViewBag.Teams = await _dbContext.Teams
    .Where(t => t.IsActive)
    .OrderBy(t => t.Name)
    .ToListAsync();
```

- [ ] **Step 8: Add Fix button to existing GroupSettingsResults view**

In `GroupSettingsResults.cshtml`, after the card-footer section (line ~100), inside each report card that has drift, add a Fix button. In the card-header `div` (line 40), after the badge, add:

```html
@if (report.HasDrift)
{
    <form asp-action="RemediateGroupSettings" method="post" class="d-inline ms-2">
        <input type="hidden" name="groupEmail" value="@report.GroupEmail" />
        <input type="hidden" name="returnUrl" value="@Url.Action("GroupSettingsResults")" />
        <button type="submit" class="btn btn-warning btn-sm">Fix</button>
    </form>
}
```

Also add a link to the All Groups page at the top, after the "Back to Admin" button:

```html
<a asp-action="AllGroups" class="btn btn-outline-primary btn-sm me-1">
    <i class="fa-solid fa-globe me-1"></i>All Domain Groups
</a>
```

- [ ] **Step 9: Verify it compiles**

Run: `dotnet build Humans.slnx`
Expected: Build succeeded.

- [ ] **Step 10: Commit**

```bash
git add src/Humans.Application/DTOs/DomainGroupInfo.cs src/Humans.Application/Interfaces/IGoogleSyncService.cs src/Humans.Infrastructure/Services/GoogleWorkspaceSyncService.cs src/Humans.Infrastructure/Services/StubGoogleSyncService.cs src/Humans.Web/Controllers/AdminController.cs src/Humans.Web/Views/Admin/AllGroups.cshtml src/Humans.Web/Views/Admin/GroupSettingsResults.cshtml
git commit -m "feat: add domain-wide All Groups page with remediation and linking (#195)

New /Admin/AllGroups page queries all Google Groups on the domain via
Admin SDK, shows each with team linkage, member count, and all 14
settings with drift highlighting. Per-group Fix button and Fix All for
bulk remediation. Link-to-Team action for unlinked groups to bring
pre-existing groups under management. Also adds Fix button to existing
GroupSettingsResults view and expands reference table to all 14 settings."
```

---

## Final Steps

### Task 11: Verify full build and update docs

- [ ] **Step 1: Full build and test**

```bash
dotnet build Humans.slnx
dotnet test Humans.slnx
```

- [ ] **Step 2: Update feature docs if affected**

Check `docs/features/07-google-integration.md` for any references to `WhoCanViewMembership: ALL_MEMBERS_CAN_VIEW` and update to `OWNERS_AND_MANAGERS`.

- [ ] **Step 3: Update todos.md**

Mark #172, #193, #173, #195 progress in `todos.md`.

- [ ] **Step 4: Final commit**

```bash
git add docs/ todos.md
git commit -m "docs: update feature docs and todos for session 1 fixes"
```
