# Feedback System Upgrade — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Upgrade the feedback system from one-directional admin triage to bidirectional conversation with unified UI, role-based access, and admin nav badges.

**Architecture:** Single unified `FeedbackController` replaces both existing controllers. New `FeedbackMessage` entity for conversation threading. Master-detail Razor view with AJAX detail loading. Timestamp-based unread tracking via `LastReporterMessageAt` / `LastAdminMessageAt`. `FeedbackAdmin` role follows existing CampAdmin/TeamsAdmin/TicketAdmin pattern.

**Tech Stack:** ASP.NET Core 9, EF Core 9 (PostgreSQL), Razor views, Bootstrap 5, xUnit + NSubstitute + AwesomeAssertions, NodaTime

**Spec:** `docs/superpowers/specs/2026-03-23-feedback-upgrade-design.md`

**Issue:** #192

---

## File Map

| Action | File | Responsibility |
|--------|------|----------------|
| Create | `src/Humans.Domain/Entities/FeedbackMessage.cs` | New conversation message entity |
| Create | `src/Humans.Infrastructure/Data/Configurations/FeedbackMessageConfiguration.cs` | EF configuration for `feedback_messages` table |
| Create | `src/Humans.Web/Views/Feedback/Index.cshtml` | Master-detail unified page |
| Create | `src/Humans.Web/Views/Feedback/_Detail.cshtml` | Detail partial for AJAX loading |
| Create | `src/Humans.Infrastructure/Migrations/YYYYMMDD_AddFeedbackMessages.cs` | Migration (auto-generated) |
| Modify | `src/Humans.Domain/Entities/FeedbackReport.cs` | Add `AdditionalContext`, timestamps; remove `AdminNotes`, `AdminResponseSentAt` |
| Modify | `src/Humans.Domain/Constants/RoleNames.cs` | Add `FeedbackAdmin` constant |
| Modify | `src/Humans.Domain/Constants/RoleGroups.cs` | Add `FeedbackAdminOrAdmin` constant |
| Modify | `src/Humans.Web/Authorization/RoleChecks.cs` | Add `IsFeedbackAdmin()` method, add to `AdminAssignableRoles`/`BoardAssignableRoles` |
| Modify | `src/Humans.Infrastructure/Data/HumansDbContext.cs` | Add `FeedbackMessages` DbSet |
| Modify | `src/Humans.Infrastructure/Data/Configurations/FeedbackReportConfiguration.cs` | New columns, drop old columns |
| Modify | `src/Humans.Application/Interfaces/IFeedbackService.cs` | Add message methods, remove notes/response methods |
| Modify | `src/Humans.Infrastructure/Services/FeedbackService.cs` | Implement message methods, update submit for `AdditionalContext` |
| Modify | `src/Humans.Web/Controllers/FeedbackController.cs` | Rewrite as unified controller with all actions |
| Modify | `src/Humans.Web/Controllers/FeedbackApiController.cs` | Add message endpoints, remove notes/response, update response shapes |
| Modify | `src/Humans.Web/Models/FeedbackViewModels.cs` | New view models for master-detail |
| Modify | `src/Humans.Web/ViewComponents/NavBadgesViewComponent.cs` | Add `feedback` queue |
| Modify | `src/Humans.Web/Views/Shared/_Layout.cshtml` | Add FeedbackAdmin nav item with badge |
| Modify | `src/Humans.Web/Views/Shared/_LoginPartial.cshtml` | Add "My Feedback" to profile dropdown |
| Modify | `src/Humans.Application/Interfaces/IEmailService.cs` | Update `SendFeedbackResponseAsync` signature to include report link |
| Modify | `src/Humans.Infrastructure/Services/OutboxEmailService.cs` | Pass link to renderer |
| Modify | `src/Humans.Infrastructure/Services/SmtpEmailService.cs` | Pass link to renderer |
| Modify | `src/Humans.Infrastructure/Services/StubEmailService.cs` | Update stub signature |
| Modify | `tests/Humans.Application.Tests/Services/FeedbackServiceTests.cs` | Update existing tests, add message tests |
| Delete | `src/Humans.Web/Controllers/AdminFeedbackController.cs` | Replaced by unified controller |
| Delete | `src/Humans.Web/Views/AdminFeedback/Index.cshtml` | Replaced by `Feedback/Index.cshtml` |
| Delete | `src/Humans.Web/Views/AdminFeedback/Detail.cshtml` | Replaced by `Feedback/_Detail.cshtml` |

---

## Task 1: Add FeedbackAdmin Role

**Files:**
- Modify: `src/Humans.Domain/Constants/RoleNames.cs`
- Modify: `src/Humans.Domain/Constants/RoleGroups.cs`
- Modify: `src/Humans.Web/Authorization/RoleChecks.cs`

- [ ] **Step 1: Add FeedbackAdmin to RoleNames.cs**

Add after `NoInfoAdmin` constant:

```csharp
/// <summary>
/// Feedback Administrator — can view all feedback reports, respond to reporters,
/// manage feedback status, and link GitHub issues.
/// </summary>
public const string FeedbackAdmin = "FeedbackAdmin";
```

- [ ] **Step 2: Add FeedbackAdminOrAdmin to RoleGroups.cs**

Add after `TicketAdminOrAdmin`:

```csharp
public const string FeedbackAdminOrAdmin = RoleNames.FeedbackAdmin + "," + RoleNames.Admin;
```

- [ ] **Step 3: Add IsFeedbackAdmin to RoleChecks.cs**

Add method:

```csharp
public static bool IsFeedbackAdmin(ClaimsPrincipal user)
{
    return IsAdmin(user) || user.IsInRole(RoleNames.FeedbackAdmin);
}
```

Add `RoleNames.FeedbackAdmin` to both `AdminAssignableRoles` and `BoardAssignableRoles` arrays.

- [ ] **Step 4: Verify build**

Run: `dotnet build Humans.slnx`
Expected: Build succeeds (role is defined but not yet used).

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Domain/Constants/RoleNames.cs src/Humans.Domain/Constants/RoleGroups.cs src/Humans.Web/Authorization/RoleChecks.cs
git commit -m "feat(feedback): add FeedbackAdmin role constant and role checks"
```

---

## Task 2: FeedbackMessage Entity + EF Configuration

**Files:**
- Create: `src/Humans.Domain/Entities/FeedbackMessage.cs`
- Create: `src/Humans.Infrastructure/Data/Configurations/FeedbackMessageConfiguration.cs`
- Modify: `src/Humans.Infrastructure/Data/HumansDbContext.cs`

- [ ] **Step 1: Create FeedbackMessage entity**

Create `src/Humans.Domain/Entities/FeedbackMessage.cs`:

```csharp
using NodaTime;

namespace Humans.Domain.Entities;

public class FeedbackMessage
{
    public Guid Id { get; init; }

    public Guid FeedbackReportId { get; init; }
    public FeedbackReport FeedbackReport { get; set; } = null!;

    public Guid? SenderUserId { get; init; }
    public User? SenderUser { get; set; }

    public string Content { get; set; } = string.Empty;

    public Instant CreatedAt { get; init; }
}
```

- [ ] **Step 2: Create EF configuration**

Create `src/Humans.Infrastructure/Data/Configurations/FeedbackMessageConfiguration.cs`:

```csharp
using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations;

public class FeedbackMessageConfiguration : IEntityTypeConfiguration<FeedbackMessage>
{
    public void Configure(EntityTypeBuilder<FeedbackMessage> builder)
    {
        builder.ToTable("feedback_messages");

        builder.HasKey(m => m.Id);

        builder.Property(m => m.Content)
            .HasMaxLength(5000)
            .IsRequired();

        builder.Property(m => m.CreatedAt).IsRequired();

        builder.HasOne(m => m.FeedbackReport)
            .WithMany(r => r.Messages)
            .HasForeignKey(m => m.FeedbackReportId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(m => m.SenderUser)
            .WithMany()
            .HasForeignKey(m => m.SenderUserId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(m => m.FeedbackReportId);
        builder.HasIndex(m => m.CreatedAt);
    }
}
```

- [ ] **Step 3: Add Messages navigation to FeedbackReport**

In `src/Humans.Domain/Entities/FeedbackReport.cs`, add at the end (before closing brace):

```csharp
public ICollection<FeedbackMessage> Messages { get; set; } = new List<FeedbackMessage>();
```

- [ ] **Step 4: Add DbSet to HumansDbContext**

In `src/Humans.Infrastructure/Data/HumansDbContext.cs`, add after `FeedbackReports` line:

```csharp
public DbSet<FeedbackMessage> FeedbackMessages => Set<FeedbackMessage>();
```

- [ ] **Step 5: Verify build**

Run: `dotnet build Humans.slnx`

- [ ] **Step 6: Commit**

```bash
git add src/Humans.Domain/Entities/FeedbackMessage.cs src/Humans.Infrastructure/Data/Configurations/FeedbackMessageConfiguration.cs src/Humans.Domain/Entities/FeedbackReport.cs src/Humans.Infrastructure/Data/HumansDbContext.cs
git commit -m "feat(feedback): add FeedbackMessage entity and EF configuration"
```

---

## Task 3: Update FeedbackReport — New Fields, Remove Old Fields

**Files:**
- Modify: `src/Humans.Domain/Entities/FeedbackReport.cs`
- Modify: `src/Humans.Infrastructure/Data/Configurations/FeedbackReportConfiguration.cs`

- [ ] **Step 1: Update FeedbackReport entity**

In `src/Humans.Domain/Entities/FeedbackReport.cs`:

**Add** these properties (after `UserAgent`):

```csharp
public string? AdditionalContext { get; set; }
```

**Add** these properties (after `GitHubIssueNumber`):

```csharp
public Instant? LastReporterMessageAt { get; set; }
public Instant? LastAdminMessageAt { get; set; }
```

**Remove** these properties:
- `AdminNotes` (line 23)
- `AdminResponseSentAt` (line 25)

- [ ] **Step 2: Update EF configuration**

In `src/Humans.Infrastructure/Data/Configurations/FeedbackReportConfiguration.cs`:

**Add** after `UserAgent` maxlength:

```csharp
builder.Property(f => f.AdditionalContext)
    .HasMaxLength(2000);
```

**Remove** the `AdminNotes` configuration (line 45-46):
```csharp
builder.Property(f => f.AdminNotes)
    .HasMaxLength(5000);
```

- [ ] **Step 3: Verify build**

Run: `dotnet build Humans.slnx`
Expected: Build errors in AdminFeedbackController, FeedbackApiController, FeedbackService, FeedbackViewModels, and tests that reference `AdminNotes` or `AdminResponseSentAt`. This is expected — we'll fix those in subsequent tasks.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Domain/Entities/FeedbackReport.cs src/Humans.Infrastructure/Data/Configurations/FeedbackReportConfiguration.cs
git commit -m "feat(feedback): add AdditionalContext, timestamps; remove AdminNotes, AdminResponseSentAt"
```

---

## Task 4: Generate EF Migration

**Files:**
- Create: `src/Humans.Infrastructure/Migrations/YYYYMMDD_FeedbackUpgrade.cs` (auto-generated)

- [ ] **Step 1: Generate migration**

Run: `dotnet ef migrations add FeedbackUpgrade --project src/Humans.Infrastructure --startup-project src/Humans.Web`

This will generate a migration that:
- Creates `feedback_messages` table
- Adds `additional_context`, `last_reporter_message_at`, `last_admin_message_at` to `feedback_reports`
- Drops `admin_notes`, `admin_response_sent_at` from `feedback_reports`

- [ ] **Step 2: Edit migration to add data migration**

In the generated migration's `Up` method, **before** the `DropColumn` calls for `admin_notes`, insert SQL to migrate existing admin notes to messages:

```csharp
// Migrate existing admin notes to feedback_messages
migrationBuilder.Sql("""
    INSERT INTO feedback_messages (id, feedback_report_id, sender_user_id, content, created_at)
    SELECT gen_random_uuid(), id, COALESCE(resolved_by_user_id, user_id), admin_notes, updated_at
    FROM feedback_reports
    WHERE admin_notes IS NOT NULL AND admin_notes <> ''
    """);
```

Also set the `last_admin_message_at` for migrated notes:

```csharp
migrationBuilder.Sql("""
    UPDATE feedback_reports SET last_admin_message_at = updated_at
    WHERE admin_notes IS NOT NULL AND admin_notes <> ''
    """);
```

- [ ] **Step 3: Verify migration compiles**

Run: `dotnet build src/Humans.Infrastructure`

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Infrastructure/Migrations/
git commit -m "feat(feedback): add FeedbackUpgrade migration with admin notes data migration"
```

---

## Task 5: Update Service Interface + Implementation

**Files:**
- Modify: `src/Humans.Application/Interfaces/IFeedbackService.cs`
- Modify: `src/Humans.Infrastructure/Services/FeedbackService.cs`

- [ ] **Step 1: Update IFeedbackService interface**

Replace the full interface content with:

```csharp
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.AspNetCore.Http;

namespace Humans.Application.Interfaces;

public interface IFeedbackService
{
    Task<FeedbackReport> SubmitFeedbackAsync(
        Guid userId, FeedbackCategory category, string description,
        string pageUrl, string? userAgent, string? additionalContext,
        IFormFile? screenshot, CancellationToken cancellationToken = default);

    Task<FeedbackReport?> GetFeedbackByIdAsync(
        Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FeedbackReport>> GetFeedbackListAsync(
        FeedbackStatus? status = null, FeedbackCategory? category = null,
        Guid? reporterUserId = null, int limit = 50,
        CancellationToken cancellationToken = default);

    Task UpdateStatusAsync(
        Guid id, FeedbackStatus status, Guid? actorUserId,
        CancellationToken cancellationToken = default);

    Task SetGitHubIssueNumberAsync(
        Guid id, int? issueNumber, CancellationToken cancellationToken = default);

    Task<FeedbackMessage> PostMessageAsync(
        Guid reportId, Guid senderUserId, string content, bool isAdmin,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FeedbackMessage>> GetMessagesAsync(
        Guid reportId, CancellationToken cancellationToken = default);

    Task<int> GetActionableCountAsync(
        CancellationToken cancellationToken = default);
}
```

**Key changes:**
- `SubmitFeedbackAsync` gains `additionalContext` parameter
- `GetFeedbackListAsync` gains optional `reporterUserId` for user-scoped queries
- Removed: `UpdateAdminNotesAsync`, `SendResponseAsync`, `GetResponseCountsAsync`, `GetResponseDetailsAsync`, `FeedbackResponseDetail`
- Added: `PostMessageAsync`, `GetMessagesAsync`, `GetActionableCountAsync`

- [ ] **Step 2: Update FeedbackService implementation**

In `src/Humans.Infrastructure/Services/FeedbackService.cs`:

**Update `SubmitFeedbackAsync`** — add `additionalContext` parameter and set on report:

```csharp
public async Task<FeedbackReport> SubmitFeedbackAsync(
    Guid userId, FeedbackCategory category, string description,
    string pageUrl, string? userAgent, string? additionalContext,
    IFormFile? screenshot, CancellationToken cancellationToken = default)
```

In the `new FeedbackReport` block, add: `AdditionalContext = additionalContext,`

**Update `GetFeedbackListAsync`** — add `reporterUserId` filter:

```csharp
public async Task<IReadOnlyList<FeedbackReport>> GetFeedbackListAsync(
    FeedbackStatus? status = null, FeedbackCategory? category = null,
    Guid? reporterUserId = null, int limit = 50,
    CancellationToken cancellationToken = default)
```

Add filter after category filter:

```csharp
if (reporterUserId.HasValue)
    query = query.Where(f => f.UserId == reporterUserId.Value);
```

**Update `GetFeedbackByIdAsync`** — include Messages:

```csharp
return await _dbContext.FeedbackReports
    .Include(f => f.User)
    .Include(f => f.ResolvedByUser)
    .Include(f => f.Messages.OrderBy(m => m.CreatedAt))
        .ThenInclude(m => m.SenderUser)
    .FirstOrDefaultAsync(f => f.Id == id, cancellationToken);
```

**Remove** `UpdateAdminNotesAsync`, `SendResponseAsync`, `GetResponseCountsAsync`, `GetResponseDetailsAsync` methods.

**Add `PostMessageAsync`:**

```csharp
public async Task<FeedbackMessage> PostMessageAsync(
    Guid reportId, Guid senderUserId, string content, bool isAdmin,
    CancellationToken cancellationToken = default)
{
    var report = await _dbContext.FeedbackReports
        .Include(f => f.User)
        .FirstOrDefaultAsync(f => f.Id == reportId, cancellationToken)
        ?? throw new InvalidOperationException($"Feedback report {reportId} not found");

    var now = _clock.GetCurrentInstant();

    var message = new FeedbackMessage
    {
        Id = Guid.NewGuid(),
        FeedbackReportId = reportId,
        SenderUserId = senderUserId,
        Content = content,
        CreatedAt = now
    };

    _dbContext.FeedbackMessages.Add(message);

    if (isAdmin)
    {
        report.LastAdminMessageAt = now;

        // Send email notification to reporter
        var user = report.User;
        var reportLink = $"/Feedback/{reportId}";
        await _emailService.SendFeedbackResponseAsync(
            user.Email!, user.DisplayName,
            report.Description, content, reportLink,
            user.PreferredLanguage, cancellationToken);
    }
    else
    {
        report.LastReporterMessageAt = now;
    }

    report.UpdatedAt = now;
    await _dbContext.SaveChangesAsync(cancellationToken);

    _logger.LogInformation("Feedback message posted on {ReportId} by {UserId} (admin: {IsAdmin})",
        reportId, senderUserId, isAdmin);

    return message;
}
```

**Add `GetMessagesAsync`:**

```csharp
public async Task<IReadOnlyList<FeedbackMessage>> GetMessagesAsync(
    Guid reportId, CancellationToken cancellationToken = default)
{
    return await _dbContext.FeedbackMessages
        .Include(m => m.SenderUser)
        .Where(m => m.FeedbackReportId == reportId)
        .OrderBy(m => m.CreatedAt)
        .AsNoTracking()
        .ToListAsync(cancellationToken);
}
```

**Add `GetActionableCountAsync`:**

```csharp
public async Task<int> GetActionableCountAsync(
    CancellationToken cancellationToken = default)
{
    return await _dbContext.FeedbackReports
        .Where(f => f.Status != FeedbackStatus.Resolved && f.Status != FeedbackStatus.WontFix)
        .CountAsync(f =>
            (f.Status == FeedbackStatus.Open && f.LastAdminMessageAt == null) ||
            (f.LastReporterMessageAt != null && (f.LastAdminMessageAt == null || f.LastReporterMessageAt > f.LastAdminMessageAt)),
            cancellationToken);
}
```

- [ ] **Step 3: Verify build compiles (expect test failures)**

Run: `dotnet build Humans.slnx`
Expected: Build errors in controllers, API controller, and tests. We'll fix those next.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Application/Interfaces/IFeedbackService.cs src/Humans.Infrastructure/Services/FeedbackService.cs
git commit -m "feat(feedback): update service layer — add messaging, remove admin notes/response"
```

---

## Task 6: Update Email Service Signature

**Files:**
- Modify: `src/Humans.Application/Interfaces/IEmailService.cs`
- Modify: `src/Humans.Infrastructure/Services/OutboxEmailService.cs`
- Modify: `src/Humans.Infrastructure/Services/SmtpEmailService.cs`
- Modify: `src/Humans.Infrastructure/Services/StubEmailService.cs`

- [ ] **Step 1: Update IEmailService interface**

Change `SendFeedbackResponseAsync` to include `reportLink` parameter:

```csharp
Task SendFeedbackResponseAsync(
    string userEmail, string userName, string originalDescription,
    string responseMessage, string reportLink, string? culture = null,
    CancellationToken cancellationToken = default);
```

- [ ] **Step 2: Update all implementations**

In each of the three email service files, update the method signature to include `string reportLink`. For `OutboxEmailService`, also pass `reportLink` to the renderer:

```csharp
var content = _renderer.RenderFeedbackResponse(userName, originalDescription, responseMessage, reportLink, culture);
```

For `SmtpEmailService` and `StubEmailService`, just update the signature (they follow the same renderer pattern or are no-ops).

- [ ] **Step 3: Update IEmailRenderer if it exists**

Search for `RenderFeedbackResponse` and update the renderer interface and implementation to accept and use `reportLink` — include it in the email HTML body as a clickable link.

- [ ] **Step 4: Verify build**

Run: `dotnet build Humans.slnx`

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Application/Interfaces/IEmailService.cs src/Humans.Infrastructure/Services/OutboxEmailService.cs src/Humans.Infrastructure/Services/SmtpEmailService.cs src/Humans.Infrastructure/Services/StubEmailService.cs
# Also add renderer files if modified
git commit -m "feat(feedback): add report link parameter to feedback email"
```

---

## Task 7: Update Tests

**Files:**
- Modify: `tests/Humans.Application.Tests/Services/FeedbackServiceTests.cs`

- [ ] **Step 1: Fix existing tests**

Update any tests that reference `AdminNotes` or `AdminResponseSentAt` — remove assertions on those fields. Update `SubmitFeedbackAsync` calls to include the new `additionalContext` parameter (pass `null` for existing tests).

Remove tests for `UpdateAdminNotesAsync`, `SendResponseAsync`, `GetResponseCountsAsync`, `GetResponseDetailsAsync`.

- [ ] **Step 2: Add PostMessageAsync tests**

```csharp
[Fact]
public async Task PostMessageAsync_AdminMessage_SetsLastAdminMessageAt_And_SendsEmail()
{
    // Arrange
    var userId = Guid.NewGuid();
    var user = new User { Id = userId, Email = "reporter@test.com", DisplayName = "Reporter" };
    _dbContext.Users.Add(user);

    var report = new FeedbackReport
    {
        Id = Guid.NewGuid(), UserId = userId, Category = FeedbackCategory.Bug,
        Description = "Test", PageUrl = "/test", Status = FeedbackStatus.Open,
        CreatedAt = _clock.GetCurrentInstant(), UpdatedAt = _clock.GetCurrentInstant()
    };
    _dbContext.FeedbackReports.Add(report);
    await _dbContext.SaveChangesAsync();

    var adminId = Guid.NewGuid();

    // Act
    var message = await _service.PostMessageAsync(report.Id, adminId, "Looking into it", isAdmin: true);

    // Assert
    message.Content.Should().Be("Looking into it");
    message.SenderUserId.Should().Be(adminId);

    var updated = await _dbContext.FeedbackReports.FindAsync(report.Id);
    updated!.LastAdminMessageAt.Should().NotBeNull();
    updated.LastReporterMessageAt.Should().BeNull();

    await _emailService.Received(1).SendFeedbackResponseAsync(
        "reporter@test.com", "Reporter", "Test", "Looking into it",
        $"/Feedback/{report.Id}", Arg.Any<string?>(), Arg.Any<CancellationToken>());
}

[Fact]
public async Task PostMessageAsync_ReporterMessage_SetsLastReporterMessageAt_NoEmail()
{
    // Arrange
    var userId = Guid.NewGuid();
    var user = new User { Id = userId, Email = "reporter@test.com", DisplayName = "Reporter" };
    _dbContext.Users.Add(user);

    var report = new FeedbackReport
    {
        Id = Guid.NewGuid(), UserId = userId, Category = FeedbackCategory.Bug,
        Description = "Test", PageUrl = "/test", Status = FeedbackStatus.Open,
        CreatedAt = _clock.GetCurrentInstant(), UpdatedAt = _clock.GetCurrentInstant()
    };
    _dbContext.FeedbackReports.Add(report);
    await _dbContext.SaveChangesAsync();

    // Act
    var message = await _service.PostMessageAsync(report.Id, userId, "More details", isAdmin: false);

    // Assert
    message.Content.Should().Be("More details");
    var updated = await _dbContext.FeedbackReports.FindAsync(report.Id);
    updated!.LastReporterMessageAt.Should().NotBeNull();
    updated.LastAdminMessageAt.Should().BeNull();

    await _emailService.DidNotReceive().SendFeedbackResponseAsync(
        Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
        Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
}

[Fact]
public async Task GetActionableCountAsync_CountsOpenWithNoReply_And_AwaitingAdmin()
{
    var userId = Guid.NewGuid();
    var user = new User { Id = userId, Email = "u@test.com", DisplayName = "U" };
    _dbContext.Users.Add(user);

    var now = _clock.GetCurrentInstant();

    // Open, no admin message → actionable
    _dbContext.FeedbackReports.Add(new FeedbackReport
    {
        Id = Guid.NewGuid(), UserId = userId, Category = FeedbackCategory.Bug,
        Description = "a", PageUrl = "/a", Status = FeedbackStatus.Open,
        CreatedAt = now, UpdatedAt = now
    });

    // Reporter replied after admin → actionable
    _dbContext.FeedbackReports.Add(new FeedbackReport
    {
        Id = Guid.NewGuid(), UserId = userId, Category = FeedbackCategory.Bug,
        Description = "b", PageUrl = "/b", Status = FeedbackStatus.Acknowledged,
        CreatedAt = now, UpdatedAt = now,
        LastAdminMessageAt = now, LastReporterMessageAt = now + Duration.FromMinutes(5)
    });

    // Resolved → not actionable
    _dbContext.FeedbackReports.Add(new FeedbackReport
    {
        Id = Guid.NewGuid(), UserId = userId, Category = FeedbackCategory.Bug,
        Description = "c", PageUrl = "/c", Status = FeedbackStatus.Resolved,
        CreatedAt = now, UpdatedAt = now, ResolvedAt = now
    });

    await _dbContext.SaveChangesAsync();

    var count = await _service.GetActionableCountAsync();
    count.Should().Be(2);
}
```

- [ ] **Step 3: Add SubmitFeedbackAsync test for AdditionalContext**

```csharp
[Fact]
public async Task SubmitFeedbackAsync_SetsAdditionalContext()
{
    var userId = Guid.NewGuid();
    _dbContext.Users.Add(new User { Id = userId, Email = "u@test.com", DisplayName = "U" });
    await _dbContext.SaveChangesAsync();

    var report = await _service.SubmitFeedbackAsync(
        userId, FeedbackCategory.Bug, "desc", "/page", "UA",
        "Volunteer, Coordinator", null);

    report.AdditionalContext.Should().Be("Volunteer, Coordinator");
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test Humans.slnx`
Expected: All tests pass.

- [ ] **Step 5: Commit**

```bash
git add tests/Humans.Application.Tests/Services/FeedbackServiceTests.cs
git commit -m "test(feedback): update tests for messaging, remove admin notes tests"
```

---

## Task 8: Update ViewModels

**Files:**
- Modify: `src/Humans.Web/Models/FeedbackViewModels.cs`

- [ ] **Step 1: Update view models**

Keep `SubmitFeedbackViewModel` as-is (the controller populates `AdditionalContext` server-side).

Replace the list/detail view models:

```csharp
public class FeedbackPageViewModel
{
    public List<FeedbackListItemViewModel> Reports { get; set; } = new();
    public FeedbackStatus? StatusFilter { get; set; }
    public FeedbackCategory? CategoryFilter { get; set; }
    public bool IsAdmin { get; set; }
    public Guid? SelectedReportId { get; set; }
}

public class FeedbackListItemViewModel
{
    public Guid Id { get; set; }
    public FeedbackCategory Category { get; set; }
    public FeedbackStatus Status { get; set; }
    public string Description { get; set; } = string.Empty;
    public string ReporterName { get; set; } = string.Empty;
    public Guid ReporterUserId { get; set; }
    public string PageUrl { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public bool HasScreenshot { get; set; }
    public int MessageCount { get; set; }
    public bool NeedsReply { get; set; }
    public int? GitHubIssueNumber { get; set; }
}

public class FeedbackDetailViewModel
{
    public Guid Id { get; set; }
    public FeedbackCategory Category { get; set; }
    public FeedbackStatus Status { get; set; }
    public string Description { get; set; } = string.Empty;
    public string PageUrl { get; set; } = string.Empty;
    public string? UserAgent { get; set; }
    public string? AdditionalContext { get; set; }
    public string? ScreenshotUrl { get; set; }
    public string ReporterName { get; set; } = string.Empty;
    public Guid ReporterUserId { get; set; }
    public int? GitHubIssueNumber { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public string? ResolvedByName { get; set; }
    public bool IsAdmin { get; set; }
    public List<FeedbackMessageViewModel> Messages { get; set; } = new();
}

public class FeedbackMessageViewModel
{
    public Guid Id { get; set; }
    public string SenderName { get; set; } = string.Empty;
    public Guid? SenderUserId { get; set; }
    public string Content { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public bool IsReporter { get; set; }
}
```

Remove `FeedbackListViewModel` (replaced by `FeedbackPageViewModel`), `UpdateFeedbackNotesModel`, `SendFeedbackResponseModel`.

Keep `UpdateFeedbackStatusModel`, `SetGitHubIssueModel` as-is.

Add for message posting:

```csharp
public class PostFeedbackMessageModel
{
    [Required]
    [StringLength(5000)]
    public string Content { get; set; } = string.Empty;
}
```

- [ ] **Step 2: Verify build**

Run: `dotnet build Humans.slnx`
Expected: Errors in controllers (they reference old view models). Fixed in next tasks.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/Models/FeedbackViewModels.cs
git commit -m "feat(feedback): update view models for master-detail layout"
```

---

## Task 9: Rewrite Unified FeedbackController

**Files:**
- Modify: `src/Humans.Web/Controllers/FeedbackController.cs`

- [ ] **Step 1: Rewrite controller**

Replace the entire file:

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Humans.Application.Interfaces;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Authorization;
using Humans.Web.Models;

namespace Humans.Web.Controllers;

[Authorize]
[Route("Feedback")]
public class FeedbackController : HumansControllerBase
{
    private readonly IFeedbackService _feedbackService;
    private readonly IStringLocalizer<SharedResource> _localizer;
    private readonly ILogger<FeedbackController> _logger;

    public FeedbackController(
        IFeedbackService feedbackService,
        UserManager<User> userManager,
        IStringLocalizer<SharedResource> localizer,
        ILogger<FeedbackController> logger)
        : base(userManager)
    {
        _feedbackService = feedbackService;
        _localizer = localizer;
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(
        FeedbackStatus? status, FeedbackCategory? category, Guid? selected)
    {
        var (userMissing, user) = await RequireCurrentUserAsync();
        if (userMissing is not null) return userMissing;

        var isAdmin = RoleChecks.IsFeedbackAdmin(User);
        Guid? reporterFilter = isAdmin ? null : user.Id;

        var reports = await _feedbackService.GetFeedbackListAsync(status, category, reporterFilter);

        var viewModel = new FeedbackPageViewModel
        {
            StatusFilter = status,
            CategoryFilter = category,
            IsAdmin = isAdmin,
            SelectedReportId = selected,
            Reports = reports.Select(r => new FeedbackListItemViewModel
            {
                Id = r.Id,
                Category = r.Category,
                Status = r.Status,
                Description = r.Description.Length > 100 ? r.Description[..100] + "..." : r.Description,
                ReporterName = r.User.DisplayName,
                ReporterUserId = r.UserId,
                PageUrl = r.PageUrl,
                CreatedAt = r.CreatedAt.ToDateTimeUtc(),
                HasScreenshot = r.ScreenshotStoragePath is not null,
                MessageCount = r.Messages.Count,
                GitHubIssueNumber = r.GitHubIssueNumber,
                NeedsReply = r.LastReporterMessageAt.HasValue &&
                    (!r.LastAdminMessageAt.HasValue || r.LastReporterMessageAt > r.LastAdminMessageAt) ||
                    (r.Status == FeedbackStatus.Open && !r.LastAdminMessageAt.HasValue)
            }).ToList()
        };

        return View(viewModel);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Detail(Guid id)
    {
        var (userMissing, user) = await RequireCurrentUserAsync();
        if (userMissing is not null) return userMissing;

        var report = await _feedbackService.GetFeedbackByIdAsync(id);
        if (report is null) return NotFound();

        var isAdmin = RoleChecks.IsFeedbackAdmin(User);
        if (!isAdmin && report.UserId != user.Id) return NotFound();

        var viewModel = MapDetailViewModel(report, isAdmin);

        if (Request.Headers.XRequestedWith == "XMLHttpRequest")
        {
            return PartialView("_Detail", viewModel);
        }

        // Direct link — render full page with this report selected
        return RedirectToAction(nameof(Index), new { selected = id });
    }

    [HttpPost("")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Submit(SubmitFeedbackViewModel model)
    {
        var (userMissing, user) = await RequireCurrentUserAsync();
        if (userMissing is not null) return userMissing;

        if (!ModelState.IsValid)
        {
            SetError(_localizer["Feedback_Error"].Value);
            return LocalRedirect(Url.IsLocalUrl(model.PageUrl) ? model.PageUrl : "/");
        }

        try
        {
            var roles = await _userManager.GetRolesAsync(user);
            var additionalContext = roles.Count > 0 ? string.Join(", ", roles.Order()) : null;

            await _feedbackService.SubmitFeedbackAsync(
                user.Id, model.Category, model.Description,
                model.PageUrl, model.UserAgent, additionalContext,
                model.Screenshot);

            SetSuccess(_localizer["Feedback_Submitted"].Value);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Feedback submission failed for user {UserId}", user.Id);
            SetError(_localizer["Feedback_Error"].Value);
        }

        return LocalRedirect(Url.IsLocalUrl(model.PageUrl) ? model.PageUrl : "/");
    }

    [HttpPost("{id}/Message")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PostMessage(Guid id, PostFeedbackMessageModel model)
    {
        var (userMissing, user) = await RequireCurrentUserAsync();
        if (userMissing is not null) return userMissing;

        var report = await _feedbackService.GetFeedbackByIdAsync(id);
        if (report is null) return NotFound();

        var isAdmin = RoleChecks.IsFeedbackAdmin(User);
        if (!isAdmin && report.UserId != user.Id) return NotFound();

        if (!ModelState.IsValid)
        {
            SetError("Message is required.");
            return RedirectToAction(nameof(Index), new { selected = id });
        }

        try
        {
            await _feedbackService.PostMessageAsync(id, user.Id, model.Content, isAdmin);
            SetSuccess("Message posted.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to post message on feedback {FeedbackId}", id);
            SetError("Failed to post message.");
        }

        return RedirectToAction(nameof(Index), new { selected = id });
    }

    [HttpPost("{id}/Status")]
    [Authorize(Roles = RoleGroups.FeedbackAdminOrAdmin)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateStatus(Guid id, UpdateFeedbackStatusModel model)
    {
        try
        {
            var (userMissing, user) = await RequireCurrentUserAsync();
            if (userMissing is not null) return userMissing;

            await _feedbackService.UpdateStatusAsync(id, model.Status, user.Id);
            SetSuccess("Status updated.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update feedback {FeedbackId} status", id);
            SetError("Failed to update status.");
        }

        return RedirectToAction(nameof(Index), new { selected = id });
    }

    [HttpPost("{id}/GitHubIssue")]
    [Authorize(Roles = RoleGroups.FeedbackAdminOrAdmin)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetGitHubIssue(Guid id, SetGitHubIssueModel model)
    {
        try
        {
            await _feedbackService.SetGitHubIssueNumberAsync(id, model.IssueNumber);
            SetSuccess("GitHub issue linked.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set GitHub issue for feedback {FeedbackId}", id);
            SetError("Failed to link GitHub issue.");
        }

        return RedirectToAction(nameof(Index), new { selected = id });
    }

    private static FeedbackDetailViewModel MapDetailViewModel(FeedbackReport report, bool isAdmin)
    {
        return new FeedbackDetailViewModel
        {
            Id = report.Id,
            Category = report.Category,
            Status = report.Status,
            Description = report.Description,
            PageUrl = report.PageUrl,
            UserAgent = report.UserAgent,
            AdditionalContext = report.AdditionalContext,
            ScreenshotUrl = report.ScreenshotStoragePath is not null
                ? $"/{report.ScreenshotStoragePath}" : null,
            ReporterName = report.User.DisplayName,
            ReporterUserId = report.UserId,
            GitHubIssueNumber = report.GitHubIssueNumber,
            CreatedAt = report.CreatedAt.ToDateTimeUtc(),
            UpdatedAt = report.UpdatedAt.ToDateTimeUtc(),
            ResolvedAt = report.ResolvedAt?.ToDateTimeUtc(),
            ResolvedByName = report.ResolvedByUser?.DisplayName,
            IsAdmin = isAdmin,
            Messages = report.Messages.Select(m => new FeedbackMessageViewModel
            {
                Id = m.Id,
                SenderName = m.SenderUser?.DisplayName ?? "Unknown",
                SenderUserId = m.SenderUserId,
                Content = m.Content,
                CreatedAt = m.CreatedAt.ToDateTimeUtc(),
                IsReporter = m.SenderUserId.HasValue && m.SenderUserId == report.UserId
            }).ToList()
        };
    }
}
```

**Note:** The `GetFeedbackListAsync` call above needs the service to include `Messages` in list queries too (for `.Count`). The existing `GetFeedbackListAsync` already includes `.Include(f => f.User)` and `.Include(f => f.ResolvedByUser)` — add `.Include(f => f.Messages)` to the chain. Since we're at ~500 users, the Include approach is fine.

- [ ] **Step 2: Update FeedbackService.GetFeedbackListAsync to include Messages**

Add `.Include(f => f.Messages)` to the query chain in `GetFeedbackListAsync`.

- [ ] **Step 3: Verify build**

Run: `dotnet build Humans.slnx`

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/Controllers/FeedbackController.cs src/Humans.Infrastructure/Services/FeedbackService.cs
git commit -m "feat(feedback): rewrite unified FeedbackController with master-detail support"
```

---

## Task 10: Update FeedbackApiController

**Files:**
- Modify: `src/Humans.Web/Controllers/FeedbackApiController.cs`

- [ ] **Step 1: Update API controller**

Replace the full file content:

```csharp
using Microsoft.AspNetCore.Mvc;
using Humans.Application.Interfaces;
using Humans.Domain.Enums;
using Humans.Web.Filters;
using Humans.Web.Models;

namespace Humans.Web.Controllers;

[ApiController]
[Route("api/feedback")]
[ServiceFilter(typeof(ApiKeyAuthFilter))]
public class FeedbackApiController : ControllerBase
{
    private readonly IFeedbackService _feedbackService;
    private readonly ILogger<FeedbackApiController> _logger;

    public FeedbackApiController(
        IFeedbackService feedbackService,
        ILogger<FeedbackApiController> logger)
    {
        _feedbackService = feedbackService;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] FeedbackStatus? status,
        [FromQuery] FeedbackCategory? category,
        [FromQuery] int limit = 50)
    {
        var reports = await _feedbackService.GetFeedbackListAsync(status, category, limit: limit);

        var result = reports.Select(r => new
        {
            r.Id,
            Category = r.Category.ToString(),
            Status = r.Status.ToString(),
            r.Description,
            r.PageUrl,
            r.UserAgent,
            r.AdditionalContext,
            ReporterName = r.User.DisplayName,
            ReporterEmail = r.User.Email,
            ReporterUserId = r.UserId,
            ReporterLanguage = r.User.PreferredLanguage,
            r.GitHubIssueNumber,
            ScreenshotUrl = r.ScreenshotStoragePath is not null ? $"/{r.ScreenshotStoragePath}" : null,
            CreatedAt = r.CreatedAt.ToDateTimeUtc(),
            UpdatedAt = r.UpdatedAt.ToDateTimeUtc(),
            LastReporterMessageAt = r.LastReporterMessageAt?.ToDateTimeUtc(),
            LastAdminMessageAt = r.LastAdminMessageAt?.ToDateTimeUtc(),
            ResolvedAt = r.ResolvedAt?.ToDateTimeUtc(),
            ResolvedByName = r.ResolvedByUser?.DisplayName,
            MessageCount = r.Messages.Count
        });

        return Ok(result);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(Guid id)
    {
        var r = await _feedbackService.GetFeedbackByIdAsync(id);
        if (r is null) return NotFound();

        return Ok(new
        {
            r.Id,
            Category = r.Category.ToString(),
            Status = r.Status.ToString(),
            r.Description,
            r.PageUrl,
            r.UserAgent,
            r.AdditionalContext,
            ReporterName = r.User.DisplayName,
            ReporterEmail = r.User.Email,
            ReporterUserId = r.UserId,
            ReporterLanguage = r.User.PreferredLanguage,
            r.GitHubIssueNumber,
            ScreenshotUrl = r.ScreenshotStoragePath is not null ? $"/{r.ScreenshotStoragePath}" : null,
            CreatedAt = r.CreatedAt.ToDateTimeUtc(),
            UpdatedAt = r.UpdatedAt.ToDateTimeUtc(),
            LastReporterMessageAt = r.LastReporterMessageAt?.ToDateTimeUtc(),
            LastAdminMessageAt = r.LastAdminMessageAt?.ToDateTimeUtc(),
            ResolvedAt = r.ResolvedAt?.ToDateTimeUtc(),
            ResolvedByName = r.ResolvedByUser?.DisplayName,
            Messages = r.Messages.Select(m => new
            {
                m.Id,
                SenderName = m.SenderUser?.DisplayName ?? "Unknown",
                m.SenderUserId,
                m.Content,
                CreatedAt = m.CreatedAt.ToDateTimeUtc(),
                IsReporter = m.SenderUserId == r.UserId
            })
        });
    }

    [HttpGet("{id}/messages")]
    public async Task<IActionResult> GetMessages(Guid id)
    {
        var report = await _feedbackService.GetFeedbackByIdAsync(id);
        if (report is null) return NotFound();

        var messages = await _feedbackService.GetMessagesAsync(id);

        return Ok(messages.Select(m => new
        {
            m.Id,
            SenderName = m.SenderUser?.DisplayName ?? "Unknown",
            m.SenderUserId,
            m.Content,
            CreatedAt = m.CreatedAt.ToDateTimeUtc(),
            IsReporter = m.SenderUserId == report.UserId
        }));
    }

    [HttpPost("{id}/messages")]
    public async Task<IActionResult> PostMessage(Guid id, [FromBody] PostFeedbackMessageModel model)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        try
        {
            // API messages are always from admin context
            var message = await _feedbackService.PostMessageAsync(id, Guid.Empty, model.Content, isAdmin: true);
            return Ok(new
            {
                message.Id,
                message.Content,
                CreatedAt = message.CreatedAt.ToDateTimeUtc()
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to post message on feedback {FeedbackId}", id);
            return StatusCode(500, new { error = "Failed to post message" });
        }
    }

    [HttpPatch("{id}/status")]
    public async Task<IActionResult> UpdateStatus(Guid id, [FromBody] UpdateFeedbackStatusModel model)
    {
        try
        {
            await _feedbackService.UpdateStatusAsync(id, model.Status, null);
            return Ok(new { success = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update feedback {FeedbackId} status", id);
            return StatusCode(500, new { error = "Failed to update status" });
        }
    }

    [HttpPatch("{id}/github-issue")]
    public async Task<IActionResult> SetGitHubIssue(Guid id, [FromBody] SetGitHubIssueModel model)
    {
        try
        {
            await _feedbackService.SetGitHubIssueNumberAsync(id, model.IssueNumber);
            return Ok(new { success = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set GitHub issue for feedback {FeedbackId}", id);
            return StatusCode(500, new { error = "Failed to set GitHub issue" });
        }
    }
}
```

- [ ] **Step 2: Verify build**

Run: `dotnet build Humans.slnx`

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/Controllers/FeedbackApiController.cs
git commit -m "feat(feedback): update API — add message endpoints, remove notes/response"
```

---

## Task 11: Delete Old Admin Feedback Files

**Files:**
- Delete: `src/Humans.Web/Controllers/AdminFeedbackController.cs`
- Delete: `src/Humans.Web/Views/AdminFeedback/Index.cshtml`
- Delete: `src/Humans.Web/Views/AdminFeedback/Detail.cshtml`

- [ ] **Step 1: Delete old files**

```bash
rm src/Humans.Web/Controllers/AdminFeedbackController.cs
rm -r src/Humans.Web/Views/AdminFeedback/
```

- [ ] **Step 2: Remove admin feedback link from Admin dashboard**

In `src/Humans.Web/Views/Admin/Index.cshtml`, find and remove the "Feedback" link in the System Operations card (around lines 44-45).

- [ ] **Step 3: Verify build**

Run: `dotnet build Humans.slnx`

- [ ] **Step 4: Commit**

```bash
git add -u src/Humans.Web/Controllers/AdminFeedbackController.cs src/Humans.Web/Views/AdminFeedback/ src/Humans.Web/Views/Admin/Index.cshtml
git commit -m "feat(feedback): remove old AdminFeedbackController and views"
```

---

## Task 12: NavBadges + Navigation Links

**Files:**
- Modify: `src/Humans.Web/ViewComponents/NavBadgesViewComponent.cs`
- Modify: `src/Humans.Web/Views/Shared/_Layout.cshtml`
- Modify: `src/Humans.Web/Views/Shared/_LoginPartial.cshtml`

- [ ] **Step 1: Update NavBadgesViewComponent**

In `src/Humans.Web/ViewComponents/NavBadgesViewComponent.cs`, update the cached counts to include feedback:

Change the cache entry type from `(Review, Voting)` tuple to `(int Review, int Voting, int Feedback)`:

```csharp
var counts = await _cache.GetOrCreateAsync(CacheKeys.NavBadgeCounts, async entry =>
{
    entry.AbsoluteExpirationRelativeToNow = CacheDuration;

    var reviewCount = await _dbContext.Profiles
        .CountAsync(p => !p.IsApproved && p.RejectedAt == null);

    var votingCount = await _dbContext.Applications
        .CountAsync(a => a.Status == ApplicationStatus.Submitted);

    var feedbackCount = await _dbContext.FeedbackReports
        .Where(f => f.Status != FeedbackStatus.Resolved && f.Status != FeedbackStatus.WontFix)
        .CountAsync(f =>
            (f.Status == FeedbackStatus.Open && f.LastAdminMessageAt == null) ||
            (f.LastReporterMessageAt != null && (f.LastAdminMessageAt == null || f.LastReporterMessageAt > f.LastAdminMessageAt)));

    return (Review: reviewCount, Voting: votingCount, Feedback: feedbackCount);
});

var count = string.Equals(queue, "review", StringComparison.OrdinalIgnoreCase)
    ? counts.Review
    : string.Equals(queue, "feedback", StringComparison.OrdinalIgnoreCase)
    ? counts.Feedback
    : counts.Voting;
```

Add `using Humans.Domain.Enums;` at the top.

- [ ] **Step 2: Add FeedbackAdmin nav link in _Layout.cshtml**

After the Tickets nav item block (around line 99), add:

```html
@if (Humans.Web.Authorization.RoleChecks.IsFeedbackAdmin(User))
{
    <li class="nav-item">
        <a class="nav-link" asp-area="" asp-controller="Feedback" asp-action="Index">Feedback @await Component.InvokeAsync("NavBadges", new { queue = "feedback" })</a>
    </li>
}
```

- [ ] **Step 3: Add "My Feedback" to profile dropdown in _LoginPartial.cshtml**

After the "Legal" link (around line 62), add:

```html
<li>
    <a class="dropdown-item d-flex align-items-center gap-2" asp-controller="Feedback" asp-action="Index">
        <i class="fa-solid fa-comment-dots fa-fw"></i> @Localizer["Nav_MyFeedback"]
    </a>
</li>
```

- [ ] **Step 4: Add localization string**

Add `Nav_MyFeedback` = "My Feedback" to the shared resource files (en, es, de, fr, it).

- [ ] **Step 5: Verify build**

Run: `dotnet build Humans.slnx`

- [ ] **Step 6: Commit**

```bash
git add src/Humans.Web/ViewComponents/NavBadgesViewComponent.cs src/Humans.Web/Views/Shared/_Layout.cshtml src/Humans.Web/Views/Shared/_LoginPartial.cshtml src/Humans.Web/Resources/
git commit -m "feat(feedback): add nav badge for FeedbackAdmin, My Feedback link in profile dropdown"
```

---

## Task 13: Master-Detail View — Index.cshtml

**Files:**
- Create: `src/Humans.Web/Views/Feedback/Index.cshtml`

- [ ] **Step 1: Create the master-detail page**

Create `src/Humans.Web/Views/Feedback/Index.cshtml`. This is the main page that shows:
- Filter bar at top (status + category dropdowns) — same pattern as `AdminFeedback/Index.cshtml`
- Left panel (40%): scrollable report list with status badges, message counts, "needs reply" indicator
- Right panel (60%): loaded via AJAX when a report is clicked (`_Detail` partial)
- JavaScript: click on list item → fetch `/Feedback/{id}` with XMLHttpRequest → inject into detail panel
- If `Model.SelectedReportId` is set, auto-load that report's detail on page load

Reference the existing `AdminFeedback/Index.cshtml` for badge styling patterns (status badges use Bootstrap: `bg-danger` for Open, `bg-info` for Acknowledged, `bg-success` for Resolved, `bg-secondary` for WontFix).

The list items should show:
- Report title: category + truncated page path (e.g., "Bug on /Teams")
- Reporter name + date
- Message count + "needs reply" indicator (red dot + text when `NeedsReply` is true)
- GitHub issue number if set
- Status badge

Key layout classes: `d-flex` for the split panel, `overflow-auto` for the list scroll, `border-end` for the panel divider.

Admin-only elements (visible when `Model.IsAdmin`):
- Filter bar shown to all, but admin gets more filter options
- Reporter name links to profile

- [ ] **Step 2: Verify build**

Run: `dotnet build Humans.slnx`

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/Views/Feedback/Index.cshtml
git commit -m "feat(feedback): add master-detail Index view"
```

---

## Task 14: Detail Partial View — _Detail.cshtml

**Files:**
- Create: `src/Humans.Web/Views/Feedback/_Detail.cshtml`

- [ ] **Step 1: Create the detail partial**

Create `src/Humans.Web/Views/Feedback/_Detail.cshtml` with `@model FeedbackDetailViewModel`.

Layout (top to bottom):
1. **Header**: report title, reporter name (linked if admin), date, page path
2. **Admin controls** (if `Model.IsAdmin`): inline status dropdown + form, GitHub issue number input + form
3. **Context line**: UserAgent + AdditionalContext (roles) — small muted text
4. **Description**: original report text in a card
5. **Screenshot**: if `Model.ScreenshotUrl` is set, show clickable thumbnail
6. **Conversation**: heading "CONVERSATION", then loop over `Model.Messages`:
   - Each message: sender name + date, content in a bordered div
   - Left border color: blue (`border-primary`) if `IsReporter`, green (`border-success`) if admin
7. **Reply form**: textarea + submit button, posts to `/Feedback/{id}/Message`

Admin controls use small inline forms (same pattern as old Detail.cshtml):
- Status: `<form asp-action="UpdateStatus" asp-route-id="@Model.Id">` with `<select>` + submit button
- GitHub issue: `<form asp-action="SetGitHubIssue" asp-route-id="@Model.Id">` with `<input type="number">` + submit button

- [ ] **Step 2: Verify build**

Run: `dotnet build Humans.slnx`

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/Views/Feedback/_Detail.cshtml
git commit -m "feat(feedback): add detail partial view with conversation thread"
```

---

## Task 15: Full Build + Test + Verify

- [ ] **Step 1: Full build**

Run: `dotnet build Humans.slnx`
Expected: Clean build, no errors or warnings.

- [ ] **Step 2: Run all tests**

Run: `dotnet test Humans.slnx`
Expected: All tests pass.

- [ ] **Step 3: Format check**

Run: `dotnet format Humans.slnx --verify-no-changes`
Expected: No formatting issues.

- [ ] **Step 4: Fix any issues found**

If build errors, test failures, or format issues are found, fix them before proceeding.

- [ ] **Step 5: Commit any fixes**

```bash
git commit -m "fix(feedback): address build/test/format issues"
```

---

## Task 16: Update Feature Spec + Localization

**Files:**
- Modify: `docs/features/27-feedback-system.md`
- Modify: localization resource files (if new strings needed beyond `Nav_MyFeedback`)

- [ ] **Step 1: Update feature spec**

Update `docs/features/27-feedback-system.md` to reflect the upgraded system:
- Unified `/Feedback` page (no longer `/Admin/Feedback`)
- Conversation history model
- FeedbackAdmin role
- Nav badge
- AdditionalContext field
- Updated route table
- Updated authorization matrix

- [ ] **Step 2: Add any remaining localization strings**

Check if any hardcoded strings in views need localization entries (e.g., "Feedback", "My Feedback", conversation labels, "needs reply").

- [ ] **Step 3: Commit**

```bash
git add docs/features/27-feedback-system.md src/Humans.Web/Resources/
git commit -m "docs(feedback): update feature spec for upgraded feedback system"
```
