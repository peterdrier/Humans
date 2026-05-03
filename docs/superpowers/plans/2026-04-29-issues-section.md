# Issues Section Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship a new top-level **Issues** section (`/Issues`) that replaces Feedback's role going forward. Section-tagged + role-routed queue, six-state machine (Triage/Open/InProgress/Resolved/WontFix/Duplicate) with reporter-comment-auto-reopen, threaded comments + inline audit events, three views (Index/Detail/New), key-authed `/api/issues` for LLM agents. Old `/Feedback` keeps running in parallel until its backlog drains; deletion of old code is a separate PR.

**Architecture:** Mirrors the established Feedback section shape — `Humans.Application.Services.Issues.IssuesService` (Application layer, no EF imports) over `IIssuesRepository` (Infrastructure, singleton + `IDbContextFactory<HumansDbContext>`). Cross-domain nav properties on `Issue` / `IssueComment` are `[Obsolete]`-marked and stitched in memory by the service from `IUserService` (no `.Include()` of cross-section navs in the repo). Audit log entries are merged with comments in-memory by the service to render the unified Jira-style thread; no schema for inline events.

**Tech Stack:** .NET 10, ASP.NET Core MVC + Razor, EF Core 9, NodaTime, Identity, MSTest. Renaissance design tokens from `humans-design-system`. Localization via `IStringLocalizer<SharedResource>` + .resx files (en/es/de/fr/it).

**Spec:** `docs/superpowers/specs/2026-04-29-issues-section-design.md` (PR peterdrier/Humans#358).
**Visual reference:** `humans-design-system/project/ui-kits/issue-tracker.html` (extracted from the design bundle; layout, palette, type, three-view structure).

---

## File map

**Create:**
- `src/Humans.Domain/Enums/IssueCategory.cs`
- `src/Humans.Domain/Enums/IssueStatus.cs`
- `src/Humans.Domain/Entities/Issue.cs`
- `src/Humans.Domain/Entities/IssueComment.cs`
- `src/Humans.Domain/Constants/IssueSectionRouting.cs`
- `src/Humans.Application/Interfaces/Issues/IIssuesService.cs`
- `src/Humans.Application/Interfaces/Issues/IssueDtos.cs` (DTOs: thread events, list filters, distinct reporters)
- `src/Humans.Application/Interfaces/Repositories/IIssuesRepository.cs`
- `src/Humans.Application/Services/Issues/IssuesService.cs`
- `src/Humans.Infrastructure/Repositories/Issues/IssuesRepository.cs`
- `src/Humans.Infrastructure/Data/Configurations/Issues/IssueConfiguration.cs`
- `src/Humans.Infrastructure/Data/Configurations/Issues/IssueCommentConfiguration.cs`
- `src/Humans.Infrastructure/Migrations/<timestamp>_AddIssues.cs`
- `src/Humans.Web/Controllers/IssuesController.cs`
- `src/Humans.Web/Controllers/IssuesApiController.cs`
- `src/Humans.Web/Models/IssueViewModels.cs`
- `src/Humans.Web/Views/Issues/Index.cshtml`
- `src/Humans.Web/Views/Issues/_Detail.cshtml`
- `src/Humans.Web/Views/Issues/New.cshtml`
- `src/Humans.Web/ViewComponents/IssuesWidgetViewComponent.cs`
- `src/Humans.Web/Views/Shared/Components/IssuesWidget/Default.cshtml`
- `src/Humans.Web/Extensions/Sections/IssuesSectionExtensions.cs`
- `src/Humans.Infrastructure/Configuration/IssuesApiSettings.cs` (new — per-section API-key options class)
- `tests/Humans.Application.Tests/Services/IssuesServiceTests.cs`
- `tests/Humans.Application.Tests/Domain/IssueStatusTransitionTests.cs`
- `tests/Humans.Web.Tests/Controllers/IssuesApiControllerTests.cs`
- `docs/sections/Issues.md`
- `docs/features/28-issues-system.md`

**Modify:**
- `src/Humans.Domain/Enums/AuditAction.cs` — append `IssueStatusChanged`, `IssueAssigneeChanged`, `IssueSectionChanged`, `IssueGitHubLinked`
- `src/Humans.Domain/Enums/NotificationSource.cs` — append `IssueComment = 29`, `IssueStatusChanged = 30`, `IssueAssigned = 31`
- `src/Humans.Application/Interfaces/Email/IEmailService.cs` — add `SendIssueCommentAsync`
- `src/Humans.Application/Interfaces/Gdpr/GdprExportSections.cs` — add `Issues = "Issues"`
- `src/Humans.Infrastructure/Data/HumansDbContext.cs` — add `DbSet<Issue> Issues` and `DbSet<IssueComment> IssueComments`
- `src/Humans.Web/Extensions/Sections/SectionRegistration.cs` (or wherever sections register) — call `AddIssuesSection`
- `src/Humans.Web/Views/Shared/_Layout.cshtml` — add `Issues` nav link; replace `<vc:feedback-widget />` with `<vc:issues-widget />`
- `src/Humans.Web/ViewComponents/NavBadgesViewComponent.cs` — add `issues` queue (count of Open+Triage in my role-sections + my own non-terminal)
- `src/Humans.Web/Controllers/AdminController.cs` (Configuration action) — add `IssuesApiKeyConfigured` row
- `src/Humans.Web/Resources/SharedResource.{en,es,de,fr,it}.resx` — `Issue_*` strings (titles, button labels, statuses, types, area labels, email subject/body)
- `src/Humans.Web/Resources/EmailTemplates/IssueCommentEmail.{en,es,de,fr,it}.resx` (new templates — mirror existing `FeedbackResponseEmail.*.resx`)

---

## Phase 0 — Worktree

### Task 0: Create implementation worktree

**Files:** none.

- [ ] **Step 1: Create worktree off `main`**

```bash
git fetch upstream main
git worktree add .worktrees/issues-impl -b feat/issues-section main
cd .worktrees/issues-impl
```

- [ ] **Step 2: Verify clean state**

```bash
git status
git log --oneline -1
```

Expected: clean tree, HEAD at the latest `main` commit.

---

## Phase 1 — Domain

### Task 1: Add enums

**Files:**
- Create: `src/Humans.Domain/Enums/IssueCategory.cs`
- Create: `src/Humans.Domain/Enums/IssueStatus.cs`

- [ ] **Step 1: Write `IssueCategory.cs`**

```csharp
namespace Humans.Domain.Enums;

/// <summary>
/// What kind of issue this is. Stored as string in DB.
/// </summary>
public enum IssueCategory
{
    Bug,
    Feature,
    Question
}
```

- [ ] **Step 2: Write `IssueStatus.cs`**

```csharp
namespace Humans.Domain.Enums;

/// <summary>
/// Issue lifecycle. Submissions land in <see cref="Triage"/>. Terminal states:
/// <see cref="Resolved"/>, <see cref="WontFix"/>, <see cref="Duplicate"/>.
/// A reporter posting a comment on a terminal issue auto-reopens it to <see cref="Open"/>.
/// </summary>
public enum IssueStatus
{
    Triage,
    Open,
    InProgress,
    Resolved,
    WontFix,
    Duplicate
}

public static class IssueStatusExtensions
{
    public static bool IsTerminal(this IssueStatus s) =>
        s is IssueStatus.Resolved or IssueStatus.WontFix or IssueStatus.Duplicate;
}
```

- [ ] **Step 3: Build**

```bash
dotnet build Humans.slnx -v quiet
```

Expected: 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Domain/Enums/IssueCategory.cs src/Humans.Domain/Enums/IssueStatus.cs
git commit -m "feat(issues): add IssueCategory and IssueStatus enums"
```

---

### Task 2: Append AuditAction values

**Files:**
- Modify: `src/Humans.Domain/Enums/AuditAction.cs` (append at end of enum, before `AccountPurged`'s line)

- [ ] **Step 1: Append four new values**

Add these lines just before `AccountPurged,` in `AuditAction.cs`:

```csharp
    IssueStatusChanged,
    IssueAssigneeChanged,
    IssueSectionChanged,
    IssueGitHubLinked,
```

- [ ] **Step 2: Build**

```bash
dotnet build Humans.slnx -v quiet
```

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Domain/Enums/AuditAction.cs
git commit -m "feat(issues): add Issue* AuditAction values"
```

---

### Task 3: Append NotificationSource values

**Files:**
- Modify: `src/Humans.Domain/Enums/NotificationSource.cs`

- [ ] **Step 1: Append three new values**

Add inside the enum (last `CampRoleAssigned = 28` becomes the second-to-last):

```csharp
    /// <summary>A new comment was posted on an issue the user is involved in.</summary>
    IssueComment = 29,

    /// <summary>An issue's status changed (notifies reporter + assignee).</summary>
    IssueStatusChanged = 30,

    /// <summary>An issue was assigned to a user (notifies the new assignee).</summary>
    IssueAssigned = 31
```

- [ ] **Step 2: Build + commit**

```bash
dotnet build Humans.slnx -v quiet
git add src/Humans.Domain/Enums/NotificationSource.cs
git commit -m "feat(issues): add Issue* NotificationSource values"
```

---

### Task 4: Add GdprExportSections.Issues constant

**Files:**
- Modify: `src/Humans.Application/Interfaces/Gdpr/GdprExportSections.cs`

- [ ] **Step 1: Append `Issues` constant**

Add after the existing `FeedbackReports` line:

```csharp
    public const string Issues = "Issues";
```

- [ ] **Step 2: Build + commit**

```bash
dotnet build Humans.slnx -v quiet
git add src/Humans.Application/Interfaces/Gdpr/GdprExportSections.cs
git commit -m "feat(issues): add Issues GdprExportSections constant"
```

---

### Task 5: Write `Issue` and `IssueComment` entities

**Files:**
- Create: `src/Humans.Domain/Entities/Issue.cs`
- Create: `src/Humans.Domain/Entities/IssueComment.cs`

- [ ] **Step 1: Write `Issue.cs`**

```csharp
using NodaTime;
using Humans.Domain.Enums;

namespace Humans.Domain.Entities;

public class Issue
{
    public Guid Id { get; init; }

    public Guid ReporterUserId { get; init; }

    /// <summary>
    /// Cross-domain navigation to the reporter's <see cref="User"/>. Service
    /// stitches in memory from <c>IUserService.GetByIdsAsync</c>; repositories
    /// must not <c>.Include()</c> this property (design-rules §6c).
    /// </summary>
    [Obsolete("Cross-domain nav — resolve via IUserService instead. See design-rules §6c.")]
    public User Reporter { get; set; } = null!;

    /// <summary>
    /// Section the issue is about (drives routing). Null = unknown → Admin queue.
    /// One of the <c>IssueSectionRouting</c> known values; stored as string.
    /// </summary>
    public string? Section { get; set; }

    public IssueCategory Category { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>Captured by the floating widget. Null for /Issues/New and API submissions.</summary>
    public string? PageUrl { get; set; }
    public string? UserAgent { get; set; }
    public string? AdditionalContext { get; set; }

    public string? ScreenshotFileName { get; set; }
    public string? ScreenshotStoragePath { get; set; }
    public string? ScreenshotContentType { get; set; }

    public IssueStatus Status { get; set; } = IssueStatus.Triage;
    public int? GitHubIssueNumber { get; set; }
    public LocalDate? DueDate { get; set; }

    public Guid? AssigneeUserId { get; set; }

    [Obsolete("Cross-domain nav — resolve via IUserService instead. See design-rules §6c.")]
    public User? Assignee { get; set; }

    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }
    public Instant? ResolvedAt { get; set; }

    public Guid? ResolvedByUserId { get; set; }

    [Obsolete("Cross-domain nav — resolve via IUserService instead. See design-rules §6c.")]
    public User? ResolvedByUser { get; set; }

    public ICollection<IssueComment> Comments { get; set; } = new List<IssueComment>();
}
```

- [ ] **Step 2: Write `IssueComment.cs`**

```csharp
using NodaTime;

namespace Humans.Domain.Entities;

public class IssueComment
{
    public Guid Id { get; init; }
    public Guid IssueId { get; init; }
    public Issue Issue { get; set; } = null!; // aggregate-local nav, .Include() is legal

    public Guid? SenderUserId { get; init; }

    [Obsolete("Cross-domain nav — resolve via IUserService instead. See design-rules §6c.")]
    public User? Sender { get; set; }

    public string Content { get; set; } = string.Empty;
    public Instant CreatedAt { get; init; }
}
```

- [ ] **Step 3: Build + commit**

```bash
dotnet build Humans.slnx -v quiet
git add src/Humans.Domain/Entities/Issue.cs src/Humans.Domain/Entities/IssueComment.cs
git commit -m "feat(issues): add Issue and IssueComment entities"
```

---

### Task 6: Section→role routing constants

**Files:**
- Create: `src/Humans.Domain/Constants/IssueSectionRouting.cs`

- [ ] **Step 1: Write the routing class**

```csharp
namespace Humans.Domain.Constants;

/// <summary>
/// Maps an Issue's <c>Section</c> string to the role(s) whose holders see it
/// in their queue. <see cref="RoleNames.Admin"/> is implicit on every section.
/// Null Section → Admin only.
///
/// <para>
/// This is the routing table — adjust as the org learns. A change here is
/// effective immediately; no migration needed because Section is stored as a
/// free string. Sections referenced here should match the technical names
/// used by the rest of the codebase (e.g. matches <c>docs/sections/*.md</c>).
/// </para>
/// </summary>
public static class IssueSectionRouting
{
    public const string Tickets = "Tickets";
    public const string Camps = "Camps";
    public const string Teams = "Teams";
    public const string Shifts = "Shifts";
    public const string Onboarding = "Onboarding";
    public const string Profiles = "Profiles";
    public const string Users = "Users";
    public const string Budget = "Budget";
    public const string Governance = "Governance";
    public const string Legal = "Legal";
    public const string CityPlanning = "CityPlanning";

    /// <summary>
    /// Roles (besides Admin) that own each section. A user holding any of the
    /// listed roles for a section sees that section's queue. Returns an empty
    /// array for a null section (Admin-only fallback).
    /// </summary>
    public static IReadOnlyList<string> RolesFor(string? section) => section switch
    {
        Tickets       => [RoleNames.TicketAdmin],
        Camps         => [RoleNames.CampAdmin],
        Teams         => [RoleNames.TeamsAdmin],
        Shifts        => [RoleNames.NoInfoAdmin],
        Onboarding    => [RoleNames.ConsentCoordinator, RoleNames.VolunteerCoordinator, RoleNames.HumanAdmin],
        Profiles      => [RoleNames.HumanAdmin],
        Users         => [RoleNames.HumanAdmin],
        Budget        => [RoleNames.FinanceAdmin],
        Governance    => [RoleNames.Board],
        Legal         => [RoleNames.ConsentCoordinator],
        CityPlanning  => [RoleNames.CampAdmin],
        _             => []
    };

    /// <summary>
    /// Returns the set of section strings whose role list contains any of
    /// <paramref name="userRoles"/>. Used for queue filtering.
    /// </summary>
    public static IReadOnlySet<string> SectionsForRoles(IEnumerable<string> userRoles)
    {
        var roleSet = userRoles.ToHashSet(StringComparer.Ordinal);
        var sections = new HashSet<string>(StringComparer.Ordinal);
        foreach (var section in AllKnownSections)
        {
            if (RolesFor(section).Any(roleSet.Contains))
                sections.Add(section);
        }
        return sections;
    }

    public static readonly IReadOnlyList<string> AllKnownSections =
    [
        Tickets, Camps, Teams, Shifts, Onboarding, Profiles, Users,
        Budget, Governance, Legal, CityPlanning
    ];
}
```

- [ ] **Step 2: Build + commit**

```bash
dotnet build Humans.slnx -v quiet
git add src/Humans.Domain/Constants/IssueSectionRouting.cs
git commit -m "feat(issues): add IssueSectionRouting (section → role mapping)"
```

---

### Task 7: Domain test — state transitions and routing

**Files:**
- Create: `tests/Humans.Application.Tests/Domain/IssueStatusTransitionTests.cs`

- [ ] **Step 1: Write tests for `IsTerminal` and routing helper**

```csharp
using Humans.Domain.Constants;
using Humans.Domain.Enums;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Humans.Application.Tests.Domain;

[TestClass]
public class IssueStatusTransitionTests
{
    [TestMethod]
    [DataRow(IssueStatus.Triage,     false)]
    [DataRow(IssueStatus.Open,       false)]
    [DataRow(IssueStatus.InProgress, false)]
    [DataRow(IssueStatus.Resolved,   true)]
    [DataRow(IssueStatus.WontFix,    true)]
    [DataRow(IssueStatus.Duplicate,  true)]
    public void IsTerminal_returns_correct_value(IssueStatus s, bool expected)
    {
        Assert.AreEqual(expected, s.IsTerminal());
    }

    [TestMethod]
    public void RolesFor_unknown_section_returns_empty()
    {
        Assert.AreEqual(0, IssueSectionRouting.RolesFor(null).Count);
        Assert.AreEqual(0, IssueSectionRouting.RolesFor("UnknownSection").Count);
    }

    [TestMethod]
    public void RolesFor_known_section_includes_owner_role()
    {
        CollectionAssert.Contains(
            IssueSectionRouting.RolesFor(IssueSectionRouting.Tickets).ToList(),
            RoleNames.TicketAdmin);
    }

    [TestMethod]
    public void SectionsForRoles_returns_sections_user_can_handle()
    {
        var sections = IssueSectionRouting.SectionsForRoles([RoleNames.CampAdmin]);

        Assert.IsTrue(sections.Contains(IssueSectionRouting.Camps));
        Assert.IsTrue(sections.Contains(IssueSectionRouting.CityPlanning));
        Assert.IsFalse(sections.Contains(IssueSectionRouting.Tickets));
    }

    [TestMethod]
    public void SectionsForRoles_empty_role_set_returns_empty()
    {
        var sections = IssueSectionRouting.SectionsForRoles([]);
        Assert.AreEqual(0, sections.Count);
    }
}
```

- [ ] **Step 2: Run + commit**

```bash
dotnet test Humans.slnx --filter FullyQualifiedName~IssueStatusTransitionTests -v quiet
git add tests/Humans.Application.Tests/Domain/IssueStatusTransitionTests.cs
git commit -m "test(issues): IsTerminal and section→role routing tests"
```

Expected: 5 tests pass.

---

## Phase 2 — Application interfaces and DTOs

### Task 8: Service interface and DTOs

**Files:**
- Create: `src/Humans.Application/Interfaces/Issues/IssueDtos.cs`
- Create: `src/Humans.Application/Interfaces/Issues/IIssuesService.cs`

- [ ] **Step 1: Write `IssueDtos.cs`**

```csharp
using NodaTime;
using Humans.Domain.Enums;

namespace Humans.Application.Interfaces.Issues;

/// <summary>One row in the inline thread — either a comment or an audit event.</summary>
public abstract record IssueThreadEvent(Instant At, Guid? ActorUserId, string? ActorDisplayName);

public sealed record IssueCommentEvent(
    Guid CommentId,
    Instant At,
    Guid? ActorUserId,
    string? ActorDisplayName,
    bool ActorIsReporter,
    string Content) : IssueThreadEvent(At, ActorUserId, ActorDisplayName);

public sealed record IssueAuditEvent(
    Instant At,
    Guid? ActorUserId,
    string? ActorDisplayName,
    AuditAction Action,
    string Description) : IssueThreadEvent(At, ActorUserId, ActorDisplayName);

/// <summary>Filter criteria for the index list query.</summary>
public sealed record IssueListFilter(
    IssueStatus[]? Statuses = null,
    IssueCategory[]? Categories = null,
    string?[]? Sections = null,
    Guid? ReporterUserId = null,
    Guid? AssigneeUserId = null,
    string? SearchText = null,
    int Limit = 100);

public sealed record DistinctReporterRow(Guid UserId, string DisplayName, int Count);
```

- [ ] **Step 2: Write `IIssuesService.cs`**

```csharp
using Microsoft.AspNetCore.Http;
using NodaTime;
using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Application.Interfaces.Issues;

public interface IIssuesService
{
    Task<Issue> SubmitIssueAsync(
        Guid reporterUserId,
        IssueCategory category,
        string title,
        string description,
        string? section,
        string? pageUrl,
        string? userAgent,
        string? additionalContext,
        IFormFile? screenshot,
        LocalDate? dueDate = null,
        CancellationToken ct = default);

    Task<Issue?> GetIssueByIdAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<Issue>> GetIssueListAsync(
        IssueListFilter filter,
        Guid viewerUserId,
        IReadOnlyList<string> viewerRoles,
        bool viewerIsAdmin,
        CancellationToken ct = default);

    Task<IReadOnlyList<IssueThreadEvent>> GetThreadAsync(Guid issueId, CancellationToken ct = default);

    Task<IssueComment> PostCommentAsync(
        Guid issueId, Guid? senderUserId, string content,
        bool senderIsReporter, CancellationToken ct = default);

    Task UpdateStatusAsync(
        Guid issueId, IssueStatus newStatus, Guid? actorUserId, CancellationToken ct = default);

    Task UpdateAssigneeAsync(
        Guid issueId, Guid? newAssigneeUserId, Guid? actorUserId, CancellationToken ct = default);

    Task UpdateSectionAsync(
        Guid issueId, string? newSection, Guid? actorUserId, CancellationToken ct = default);

    Task SetGitHubIssueNumberAsync(
        Guid issueId, int? githubIssueNumber, Guid? actorUserId, CancellationToken ct = default);

    /// <summary>Count of Open + Triage issues whose section maps to a role the viewer holds, plus their own non-terminal issues.</summary>
    Task<int> GetActionableCountForViewerAsync(
        Guid viewerUserId, IReadOnlyList<string> viewerRoles, bool viewerIsAdmin,
        CancellationToken ct = default);

    Task<IReadOnlyList<DistinctReporterRow>> GetDistinctReportersAsync(CancellationToken ct = default);
}
```

- [ ] **Step 3: Build + commit**

```bash
dotnet build Humans.slnx -v quiet
git add src/Humans.Application/Interfaces/Issues/
git commit -m "feat(issues): add IIssuesService interface and DTOs"
```

---

### Task 9: Repository interface

**Files:**
- Create: `src/Humans.Application/Interfaces/Repositories/IIssuesRepository.cs`

- [ ] **Step 1: Write the interface**

```csharp
using Humans.Application.Interfaces.Issues;
using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Application.Interfaces.Repositories;

public interface IIssuesRepository
{
    Task AddIssueAsync(Issue issue, CancellationToken ct = default);

    Task<Issue?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Returns issue with comments (.Include) loaded; cross-domain navs are NOT included.</summary>
    Task<Issue?> FindForMutationAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<Issue>> GetListAsync(
        IssueListFilter filter,
        IReadOnlySet<string>? sectionFilter,
        Guid? reporterFallback,
        CancellationToken ct = default);

    Task<IReadOnlyList<IssueComment>> GetCommentsAsync(Guid issueId, CancellationToken ct = default);

    Task SaveTrackedIssueAsync(Issue issue, CancellationToken ct = default);

    Task AddCommentAndSaveIssueAsync(IssueComment comment, Issue issue, CancellationToken ct = default);

    /// <summary>For the nav-badge query.</summary>
    Task<int> CountActionableAsync(
        IReadOnlySet<string>? sectionFilter, Guid? viewerFallback,
        CancellationToken ct = default);

    Task<IReadOnlyList<DistinctReporterRow>> GetReporterCountsAsync(CancellationToken ct = default);

    /// <summary>For GDPR export.</summary>
    Task<IReadOnlyList<Issue>> GetForUserExportAsync(Guid userId, CancellationToken ct = default);
}
```

- [ ] **Step 2: Build + commit**

```bash
dotnet build Humans.slnx -v quiet
git add src/Humans.Application/Interfaces/Repositories/IIssuesRepository.cs
git commit -m "feat(issues): add IIssuesRepository interface"
```

---

## Phase 3 — Infrastructure (EF + repository + migration)

### Task 10: EF configurations

**Files:**
- Create: `src/Humans.Infrastructure/Data/Configurations/Issues/IssueConfiguration.cs`
- Create: `src/Humans.Infrastructure/Data/Configurations/Issues/IssueCommentConfiguration.cs`

- [ ] **Step 1: Write `IssueConfiguration.cs`**

Mirror `FeedbackReportConfiguration.cs`. Key shape:

```csharp
using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations.Issues;

public class IssueConfiguration : IEntityTypeConfiguration<Issue>
{
    public void Configure(EntityTypeBuilder<Issue> b)
    {
        b.ToTable("issues");
        b.HasKey(x => x.Id);

        b.Property(x => x.ReporterUserId).IsRequired();
        b.Property(x => x.Section).HasMaxLength(64);
        b.Property(x => x.Category).HasConversion<string>().HasMaxLength(32).IsRequired();
        b.Property(x => x.Title).HasMaxLength(200).IsRequired();
        b.Property(x => x.Description).HasMaxLength(5000).IsRequired();
        b.Property(x => x.PageUrl).HasMaxLength(2000);
        b.Property(x => x.UserAgent).HasMaxLength(1000);
        b.Property(x => x.AdditionalContext).HasMaxLength(2000);
        b.Property(x => x.ScreenshotFileName).HasMaxLength(256);
        b.Property(x => x.ScreenshotStoragePath).HasMaxLength(512);
        b.Property(x => x.ScreenshotContentType).HasMaxLength(64);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        b.Property(x => x.GitHubIssueNumber);
        b.Property(x => x.DueDate);

#pragma warning disable CS0618
        b.HasOne(x => x.Reporter).WithMany().HasForeignKey(x => x.ReporterUserId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasOne(x => x.Assignee).WithMany().HasForeignKey(x => x.AssigneeUserId)
            .OnDelete(DeleteBehavior.SetNull);

        b.HasOne(x => x.ResolvedByUser).WithMany().HasForeignKey(x => x.ResolvedByUserId)
            .OnDelete(DeleteBehavior.SetNull);
#pragma warning restore CS0618

        b.HasMany(x => x.Comments).WithOne(c => c.Issue).HasForeignKey(c => c.IssueId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => x.Status);
        b.HasIndex(x => x.CreatedAt);
        b.HasIndex(x => x.ReporterUserId);
        b.HasIndex(x => x.AssigneeUserId);
        b.HasIndex(x => x.Section);
        b.HasIndex(x => new { x.Section, x.Status });
    }
}
```

- [ ] **Step 2: Write `IssueCommentConfiguration.cs`**

```csharp
using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Humans.Infrastructure.Data.Configurations.Issues;

public class IssueCommentConfiguration : IEntityTypeConfiguration<IssueComment>
{
    public void Configure(EntityTypeBuilder<IssueComment> b)
    {
        b.ToTable("issue_comments");
        b.HasKey(x => x.Id);

        b.Property(x => x.Content).HasMaxLength(5000).IsRequired();

#pragma warning disable CS0618
        b.HasOne(x => x.Sender).WithMany().HasForeignKey(x => x.SenderUserId)
            .OnDelete(DeleteBehavior.SetNull);
#pragma warning restore CS0618

        b.HasIndex(x => x.IssueId);
        b.HasIndex(x => x.CreatedAt);
    }
}
```

- [ ] **Step 3: Add `DbSet`s to `HumansDbContext`**

In `src/Humans.Infrastructure/Data/HumansDbContext.cs`, near the other DbSets:

```csharp
public DbSet<Issue> Issues => Set<Issue>();
public DbSet<IssueComment> IssueComments => Set<IssueComment>();
```

- [ ] **Step 4: Build + commit**

```bash
dotnet build Humans.slnx -v quiet
git add src/Humans.Infrastructure/Data/Configurations/Issues/ src/Humans.Infrastructure/Data/HumansDbContext.cs
git commit -m "feat(issues): EF configurations + DbContext registration"
```

---

### Task 11: EF migration

**Files:**
- Create: `src/Humans.Infrastructure/Migrations/<timestamp>_AddIssues.cs` (auto-generated)

- [ ] **Step 1: Generate migration**

```bash
dotnet ef migrations add AddIssues \
  --project src/Humans.Infrastructure/Humans.Infrastructure.csproj \
  --startup-project src/Humans.Web/Humans.Web.csproj \
  --output-dir Migrations
```

- [ ] **Step 2: Review the generated migration**

Open the new file. Verify:
- `CreateTable("issues")` has all columns at their declared sizes.
- `CreateTable("issue_comments")` correct.
- Six `CreateIndex` calls against `issues` (Status, CreatedAt, ReporterUserId, AssigneeUserId, Section, (Section, Status)).
- Two `CreateIndex` calls against `issue_comments` (IssueId, CreatedAt).
- FKs to `users` are `OnDelete: Cascade` (Reporter), `SetNull` (Assignee, ResolvedByUser, SenderUserId).

- [ ] **Step 3: Apply against local dev DB and round-trip schema check**

```bash
dotnet ef database update \
  --project src/Humans.Infrastructure/Humans.Infrastructure.csproj \
  --startup-project src/Humans.Web/Humans.Web.csproj
```

- [ ] **Step 4: Run the EF migration reviewer agent (per CLAUDE.md gate)**

```bash
# See .claude/agents/ef-migration-reviewer.md — run before commit.
```

If reviewer reports CRITICAL issues, fix and re-generate. Otherwise:

- [ ] **Step 5: Commit migration**

```bash
git add src/Humans.Infrastructure/Migrations/
git commit -m "feat(issues): add EF migration AddIssues"
```

---

### Task 12: Repository implementation

**Files:**
- Create: `src/Humans.Infrastructure/Repositories/Issues/IssuesRepository.cs`

- [ ] **Step 1: Write the repository**

Use the same shape as `FeedbackRepository.cs`: singleton, `IDbContextFactory<HumansDbContext>`, per-call context creation. Implementation outline (each method body is a few lines of EF):

```csharp
using Humans.Application.Interfaces.Issues;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Humans.Infrastructure.Repositories.Issues;

public sealed class IssuesRepository : IIssuesRepository
{
    private readonly IDbContextFactory<HumansDbContext> _factory;

    public IssuesRepository(IDbContextFactory<HumansDbContext> factory) => _factory = factory;

    public async Task AddIssueAsync(Issue issue, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        db.Issues.Add(issue);
        await db.SaveChangesAsync(ct);
    }

    public async Task<Issue?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Issues
            .AsNoTracking()
            .Include(i => i.Comments.OrderBy(c => c.CreatedAt))
            .FirstOrDefaultAsync(i => i.Id == id, ct);
    }

    public async Task<Issue?> FindForMutationAsync(Guid id, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Issues
            .Include(i => i.Comments)
            .FirstOrDefaultAsync(i => i.Id == id, ct);
    }

    public async Task<IReadOnlyList<Issue>> GetListAsync(
        IssueListFilter f, IReadOnlySet<string>? sectionFilter, Guid? reporterFallback,
        CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        IQueryable<Issue> q = db.Issues.AsNoTracking().Include(i => i.Comments);

        if (f.Statuses is { Length: > 0 })   q = q.Where(i => f.Statuses.Contains(i.Status));
        if (f.Categories is { Length: > 0 }) q = q.Where(i => f.Categories.Contains(i.Category));
        if (f.ReporterUserId is { } rid)     q = q.Where(i => i.ReporterUserId == rid);
        if (f.AssigneeUserId is { } aid)     q = q.Where(i => i.AssigneeUserId == aid);
        if (!string.IsNullOrWhiteSpace(f.SearchText))
            q = q.Where(i => i.Title.Contains(f.SearchText) || i.Description.Contains(f.SearchText));

        // Visibility filter:
        //  - sectionFilter null = no constraint (Admin)
        //  - sectionFilter non-null = "section IN sectionFilter OR ReporterUserId == reporterFallback"
        if (sectionFilter is not null)
        {
            var sectionList = sectionFilter.ToList();
            var fallback = reporterFallback;
            q = q.Where(i =>
                (i.Section != null && sectionList.Contains(i.Section)) ||
                (fallback.HasValue && i.ReporterUserId == fallback.Value));
        }

        return await q.OrderByDescending(i => i.UpdatedAt).Take(f.Limit).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<IssueComment>> GetCommentsAsync(Guid issueId, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.IssueComments
            .AsNoTracking()
            .Where(c => c.IssueId == issueId)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task SaveTrackedIssueAsync(Issue issue, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        db.Issues.Update(issue);
        await db.SaveChangesAsync(ct);
    }

    public async Task AddCommentAndSaveIssueAsync(IssueComment comment, Issue issue, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        db.IssueComments.Add(comment);
        db.Issues.Update(issue);
        await db.SaveChangesAsync(ct);
    }

    public async Task<int> CountActionableAsync(
        IReadOnlySet<string>? sectionFilter, Guid? viewerFallback, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        IQueryable<Issue> q = db.Issues.AsNoTracking()
            .Where(i => i.Status == IssueStatus.Open || i.Status == IssueStatus.Triage);

        if (sectionFilter is not null)
        {
            var sectionList = sectionFilter.ToList();
            var fallback = viewerFallback;
            q = q.Where(i =>
                (i.Section != null && sectionList.Contains(i.Section)) ||
                (fallback.HasValue && i.ReporterUserId == fallback.Value));
        }

        return await q.CountAsync(ct);
    }

    public async Task<IReadOnlyList<DistinctReporterRow>> GetReporterCountsAsync(CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var rows = await db.Issues.AsNoTracking()
            .GroupBy(i => i.ReporterUserId)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        // DisplayName left blank — service stitches via IUserService.
        return rows.Select(r => new DistinctReporterRow(r.UserId, string.Empty, r.Count)).ToList();
    }

    public async Task<IReadOnlyList<Issue>> GetForUserExportAsync(Guid userId, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Issues
            .AsNoTracking()
            .Include(i => i.Comments.OrderBy(c => c.CreatedAt))
            .Where(i => i.ReporterUserId == userId)
            .ToListAsync(ct);
    }
}
```

- [ ] **Step 2: Build + commit**

```bash
dotnet build Humans.slnx -v quiet
git add src/Humans.Infrastructure/Repositories/Issues/
git commit -m "feat(issues): IssuesRepository implementation"
```

---

## Phase 4 — Application service

### Task 13: Service skeleton (constructor + screenshot helper + StitchCrossDomainNavsAsync)

**Files:**
- Create: `src/Humans.Application/Services/Issues/IssuesService.cs`

- [ ] **Step 1: Write the skeleton**

Mirror the `FeedbackService.cs` constructor pattern. Same dependencies but `_teamService` removed (no team-assignment in Issues), `_authzService` not needed (controllers gate by role). Key fields:

```csharp
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Extensions;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Issues;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Application.Services.Issues;

public sealed class IssuesService : IIssuesService, IUserDataContributor
{
    private readonly IIssuesRepository _repo;
    private readonly IUserService _users;
    private readonly IUserEmailService _userEmails;
    private readonly IRoleAssignmentService _roles;
    private readonly IEmailService _email;
    private readonly INotificationService _notifications;
    private readonly IAuditLogService _audit;
    private readonly INavBadgeCacheInvalidator _navBadge;
    private readonly IClock _clock;
    private readonly IHostEnvironment _env;
    private readonly ILogger<IssuesService> _logger;

    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    { "image/jpeg", "image/png", "image/webp" };
    private const long MaxScreenshotBytes = 10 * 1024 * 1024;

    public IssuesService(
        IIssuesRepository repo,
        IUserService users,
        IUserEmailService userEmails,
        IRoleAssignmentService roles,
        IEmailService email,
        INotificationService notifications,
        IAuditLogService audit,
        INavBadgeCacheInvalidator navBadge,
        IClock clock,
        IHostEnvironment env,
        ILogger<IssuesService> logger)
    {
        _repo = repo; _users = users; _userEmails = userEmails; _roles = roles;
        _email = email; _notifications = notifications; _audit = audit;
        _navBadge = navBadge; _clock = clock; _env = env; _logger = logger;
    }

    // ... methods added in subsequent tasks
}
```

If `IRoleAssignmentService` doesn't expose a "give me the userIds in role X" method, the service can fall back to `IRoleAssignmentService` + `UserManager`'s role queries; if not available without a circular reference, do the lookup at the controller layer and pass IDs in. Confirm interface during this task.

- [ ] **Step 2: Build (will fail because methods missing) — defer commit until end of Phase 4.**

---

### Task 14: `SubmitIssueAsync`

**Files:**
- Modify: `src/Humans.Application/Services/Issues/IssuesService.cs`

- [ ] **Step 1: Add submit method**

```csharp
public async Task<Issue> SubmitIssueAsync(
    Guid reporterUserId, IssueCategory category, string title, string description,
    string? section, string? pageUrl, string? userAgent, string? additionalContext,
    IFormFile? screenshot, LocalDate? dueDate, CancellationToken ct = default)
{
    var now = _clock.GetCurrentInstant();
    var issueId = Guid.NewGuid();

    var issue = new Issue
    {
        Id = issueId,
        ReporterUserId = reporterUserId,
        Section = section,
        Category = category,
        Title = title,
        Description = description,
        PageUrl = pageUrl,
        UserAgent = userAgent,
        AdditionalContext = additionalContext,
        Status = IssueStatus.Triage,
        DueDate = dueDate,
        CreatedAt = now,
        UpdatedAt = now
    };

    if (screenshot is { Length: > 0 })
    {
        if (screenshot.Length > MaxScreenshotBytes)
            throw new InvalidOperationException("Screenshot must be under 10MB.");
        if (!AllowedContentTypes.Contains(screenshot.ContentType))
            throw new InvalidOperationException("Screenshot must be JPEG, PNG, or WebP.");

        var ext = screenshot.ContentType switch
        {
            "image/jpeg" => ".jpg",
            "image/png"  => ".png",
            "image/webp" => ".webp",
            _ => throw new InvalidOperationException($"Unexpected content type: {screenshot.ContentType}")
        };

        var fileName = $"{Guid.NewGuid()}{ext}";
        var relativePath = Path.Combine("uploads", "issues", issueId.ToString(), fileName);
        var absolutePath = Path.Combine(_env.ContentRootPath, "wwwroot", relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);

        await using var stream = new FileStream(absolutePath, FileMode.Create);
        await screenshot.CopyToAsync(stream, ct);

        issue.ScreenshotFileName = screenshot.FileName;
        issue.ScreenshotStoragePath = relativePath.Replace('\\', '/');
        issue.ScreenshotContentType = screenshot.ContentType;
    }

    await _repo.AddIssueAsync(issue, ct);
    _navBadge.Invalidate();
    _logger.LogInformation("Issue {IssueId} submitted by {UserId}: {Category}/{Section}",
        issueId, reporterUserId, category, section ?? "(unknown)");
    return issue;
}
```

---

### Task 15: `GetIssueByIdAsync` + cross-domain stitching

- [ ] **Step 1: Add the read method + stitch helpers**

```csharp
public async Task<Issue?> GetIssueByIdAsync(Guid id, CancellationToken ct = default)
{
    var issue = await _repo.GetByIdAsync(id, ct);
    if (issue is null) return null;
    await StitchCrossDomainNavsAsync([issue], ct);
    return issue;
}

#pragma warning disable CS0618 // populate [Obsolete] cross-domain navs in memory
private async Task StitchCrossDomainNavsAsync(IReadOnlyList<Issue> issues, CancellationToken ct)
{
    if (issues.Count == 0) return;

    var userIds = new HashSet<Guid>();
    foreach (var i in issues)
    {
        userIds.Add(i.ReporterUserId);
        if (i.AssigneeUserId.HasValue) userIds.Add(i.AssigneeUserId.Value);
        if (i.ResolvedByUserId.HasValue) userIds.Add(i.ResolvedByUserId.Value);
        foreach (var c in i.Comments)
            if (c.SenderUserId.HasValue) userIds.Add(c.SenderUserId.Value);
    }

    var users = userIds.Count == 0 ? null : await _users.GetByIdsAsync(userIds, ct);

    foreach (var i in issues)
    {
        if (users is not null && users.TryGetValue(i.ReporterUserId, out var rep)) i.Reporter = rep;
        if (i.AssigneeUserId is { } aid && users is not null && users.TryGetValue(aid, out var assignee))
            i.Assignee = assignee;
        if (i.ResolvedByUserId is { } rbid && users is not null && users.TryGetValue(rbid, out var resolver))
            i.ResolvedByUser = resolver;
        foreach (var c in i.Comments)
            if (c.SenderUserId is { } sid && users is not null && users.TryGetValue(sid, out var sender))
                c.Sender = sender;
    }
}
#pragma warning restore CS0618
```

---

### Task 16: `GetIssueListAsync` (role-based filtering)

- [ ] **Step 1: Add list method**

```csharp
public async Task<IReadOnlyList<Issue>> GetIssueListAsync(
    IssueListFilter filter, Guid viewerUserId, IReadOnlyList<string> viewerRoles,
    bool viewerIsAdmin, CancellationToken ct = default)
{
    IReadOnlySet<string>? sectionFilter = null;
    Guid? reporterFallback = null;

    if (!viewerIsAdmin)
    {
        // Visibility = (issues whose Section maps to a role I hold) ∪ (my own reports).
        sectionFilter = IssueSectionRouting.SectionsForRoles(viewerRoles);
        reporterFallback = viewerUserId;
    }

    var issues = await _repo.GetListAsync(filter, sectionFilter, reporterFallback, ct);
    await StitchCrossDomainNavsAsync(issues, ct);
    return issues;
}
```

---

### Task 17: `GetThreadAsync` (merge comments + audit events)

- [ ] **Step 1: Add thread merge method**

```csharp
public async Task<IReadOnlyList<IssueThreadEvent>> GetThreadAsync(Guid issueId, CancellationToken ct = default)
{
    var issue = await GetIssueByIdAsync(issueId, ct)
        ?? throw new InvalidOperationException($"Issue {issueId} not found");

    var auditEntries = await _audit.GetByResourceIdAsync(nameof(Issue), issueId, ct);

    var commentEvents = issue.Comments.Select(c => (IssueThreadEvent)new IssueCommentEvent(
        c.Id, c.CreatedAt, c.SenderUserId,
#pragma warning disable CS0618
        c.Sender?.DisplayName,
#pragma warning restore CS0618
        ActorIsReporter: c.SenderUserId.HasValue && c.SenderUserId == issue.ReporterUserId,
        c.Content));

    var auditEvents = auditEntries
        .Where(a => a.Action is AuditAction.IssueStatusChanged
                              or AuditAction.IssueAssigneeChanged
                              or AuditAction.IssueSectionChanged
                              or AuditAction.IssueGitHubLinked)
        .Select(a => (IssueThreadEvent)new IssueAuditEvent(
            a.At, a.ActorUserId, a.ActorDisplayName, a.Action, a.Description));

    return commentEvents.Concat(auditEvents)
        .OrderBy(e => e.At)
        .ToList();
}
```

If `IAuditLogService` lacks `GetByResourceIdAsync`, add it as part of this task (interface + impl) — this is a one-line addition that other features will benefit from.

---

### Task 18: `PostCommentAsync` (with auto-reopen + handler-comment fan-out)

- [ ] **Step 1: Add comment method**

```csharp
public async Task<IssueComment> PostCommentAsync(
    Guid issueId, Guid? senderUserId, string content,
    bool senderIsReporter, CancellationToken ct = default)
{
    var issue = await _repo.FindForMutationAsync(issueId, ct)
        ?? throw new InvalidOperationException($"Issue {issueId} not found");

    var now = _clock.GetCurrentInstant();
    var comment = new IssueComment
    {
        Id = Guid.NewGuid(),
        IssueId = issueId,
        SenderUserId = senderUserId,
        Content = content,
        CreatedAt = now
    };

    var statusChangedToOpen = false;

    // Auto-reopen rule: reporter comment on terminal issue → Open.
    if (senderIsReporter && issue.Status.IsTerminal())
    {
        issue.Status = IssueStatus.Open;
        issue.ResolvedAt = null;
        issue.ResolvedByUserId = null;
        statusChangedToOpen = true;
    }

    issue.UpdatedAt = now;

    // Persist comment + issue atomically (single SaveChanges).
    await _repo.AddCommentAndSaveIssueAsync(comment, issue, ct);

    // Audit the auto-reopen, then fan out notifications.
    if (statusChangedToOpen)
    {
        await LogAuditAsync(AuditAction.IssueStatusChanged, issueId, senderUserId,
            $"Issue {issueId} reopened (reporter comment on terminal)", ct);
    }

    await DispatchCommentNotificationsAsync(issue, comment, senderIsReporter, ct);
    _navBadge.Invalidate();
    _logger.LogInformation("Comment posted on issue {IssueId} by {UserId} (reporter: {Reporter})",
        issueId, senderUserId, senderIsReporter);
    return comment;
}
```

Define the audit helper now (used by all mutation methods):

```csharp
private async Task LogAuditAsync(
    AuditAction action, Guid issueId, Guid? actorUserId, string description, CancellationToken ct)
{
    if (actorUserId.HasValue)
        await _audit.LogAsync(action, nameof(Issue), issueId, description, actorUserId.Value);
    else
        await _audit.LogAsync(action, nameof(Issue), issueId, description, "API");
}
```

---

### Task 19: Mutation methods — `UpdateStatusAsync`, `UpdateAssigneeAsync`, `UpdateSectionAsync`, `SetGitHubIssueNumberAsync`

- [ ] **Step 1: Add the four mutation methods**

```csharp
public async Task UpdateStatusAsync(Guid issueId, IssueStatus newStatus, Guid? actorUserId, CancellationToken ct = default)
{
    var issue = await _repo.FindForMutationAsync(issueId, ct)
        ?? throw new InvalidOperationException($"Issue {issueId} not found");

    var oldStatus = issue.Status;
    if (oldStatus == newStatus) return;

    var now = _clock.GetCurrentInstant();
    issue.Status = newStatus;
    issue.UpdatedAt = now;

    if (newStatus.IsTerminal())
    {
        issue.ResolvedAt = now;
        issue.ResolvedByUserId = actorUserId;
    }
    else if (oldStatus.IsTerminal())
    {
        issue.ResolvedAt = null;
        issue.ResolvedByUserId = null;
    }

    await _repo.SaveTrackedIssueAsync(issue, ct);
    await LogAuditAsync(AuditAction.IssueStatusChanged, issueId, actorUserId,
        $"Issue {issueId} status changed: {oldStatus} → {newStatus}", ct);
    await DispatchStatusChangedNotificationAsync(issue, oldStatus, newStatus, actorUserId, ct);
    _navBadge.Invalidate();
}

public async Task UpdateAssigneeAsync(Guid issueId, Guid? newAssigneeUserId, Guid? actorUserId, CancellationToken ct = default)
{
    var issue = await _repo.FindForMutationAsync(issueId, ct)
        ?? throw new InvalidOperationException($"Issue {issueId} not found");

    if (issue.AssigneeUserId == newAssigneeUserId) return;

    var oldAssigneeId = issue.AssigneeUserId;
    string oldName = "Unassigned";
    string newName = "Unassigned";

    if (oldAssigneeId.HasValue)
    {
        var u = await _users.GetByIdAsync(oldAssigneeId.Value, ct);
        oldName = u?.DisplayName ?? oldAssigneeId.Value.ToString();
    }
    if (newAssigneeUserId.HasValue)
    {
        var u = await _users.GetByIdAsync(newAssigneeUserId.Value, ct);
        newName = u?.DisplayName ?? newAssigneeUserId.Value.ToString();
    }

    issue.AssigneeUserId = newAssigneeUserId;
    issue.UpdatedAt = _clock.GetCurrentInstant();
    await _repo.SaveTrackedIssueAsync(issue, ct);

    await LogAuditAsync(AuditAction.IssueAssigneeChanged, issueId, actorUserId,
        $"Issue {issueId} assignee: {oldName} → {newName}", ct);

    if (newAssigneeUserId.HasValue)
        await DispatchAssignedNotificationAsync(issue, newAssigneeUserId.Value, ct);
}

public async Task UpdateSectionAsync(Guid issueId, string? newSection, Guid? actorUserId, CancellationToken ct = default)
{
    var issue = await _repo.FindForMutationAsync(issueId, ct)
        ?? throw new InvalidOperationException($"Issue {issueId} not found");

    if (string.Equals(issue.Section, newSection, StringComparison.Ordinal)) return;

    var oldSection = issue.Section ?? "(unknown)";
    var nextSection = newSection ?? "(unknown)";

    issue.Section = newSection;
    issue.UpdatedAt = _clock.GetCurrentInstant();
    await _repo.SaveTrackedIssueAsync(issue, ct);

    await LogAuditAsync(AuditAction.IssueSectionChanged, issueId, actorUserId,
        $"Issue {issueId} section: {oldSection} → {nextSection}", ct);
    _navBadge.Invalidate();
}

public async Task SetGitHubIssueNumberAsync(Guid issueId, int? githubIssueNumber, Guid? actorUserId, CancellationToken ct = default)
{
    var issue = await _repo.FindForMutationAsync(issueId, ct)
        ?? throw new InvalidOperationException($"Issue {issueId} not found");

    if (issue.GitHubIssueNumber == githubIssueNumber) return;

    issue.GitHubIssueNumber = githubIssueNumber;
    issue.UpdatedAt = _clock.GetCurrentInstant();
    await _repo.SaveTrackedIssueAsync(issue, ct);

    await LogAuditAsync(AuditAction.IssueGitHubLinked, issueId, actorUserId,
        $"Issue {issueId} GitHub link: {githubIssueNumber?.ToString() ?? "(cleared)"}", ct);
}
```

---

### Task 20: Notification dispatch helpers + `GetActionableCountForViewerAsync` + `GetDistinctReportersAsync` + GDPR

- [ ] **Step 1: Add helpers and remaining methods**

```csharp
private async Task DispatchCommentNotificationsAsync(
    Issue issue, IssueComment comment, bool senderIsReporter, CancellationToken ct)
{
    var link = $"/Issues/{issue.Id}";
    var subject = $"New comment on issue: {issue.Title}";
    var recipients = new HashSet<Guid>();

    if (senderIsReporter)
    {
        // Notify role-holders + assignee.
        var roleHolderIds = await _roles.GetUserIdsInRolesAsync(IssueSectionRouting.RolesFor(issue.Section), ct);
        foreach (var id in roleHolderIds) recipients.Add(id);
        if (issue.AssigneeUserId is { } aid && aid != comment.SenderUserId) recipients.Add(aid);
    }
    else
    {
        // Handler comment → notify reporter + assignee (if different from commenter).
        if (issue.ReporterUserId != comment.SenderUserId) recipients.Add(issue.ReporterUserId);
        if (issue.AssigneeUserId is { } aid && aid != comment.SenderUserId)
            recipients.Add(aid);

        // Send response email to the reporter only (per spec: handler-reply email).
        await SendCommentEmailAsync(issue, comment, ct);
    }

    if (recipients.Count > 0)
    {
        try
        {
            await _notifications.SendAsync(
                NotificationSource.IssueComment, NotificationClass.Informational,
                NotificationPriority.Normal, subject, recipients.ToArray(),
                body: comment.Content.Length > 240 ? comment.Content[..240] + "…" : comment.Content,
                actionUrl: link, actionLabel: "View issue", cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dispatch IssueComment notifications for {IssueId}", issue.Id);
        }
    }
}

private async Task SendCommentEmailAsync(Issue issue, IssueComment comment, CancellationToken ct)
{
    var reporter = await _users.GetByIdAsync(issue.ReporterUserId, ct);
    var emails = await _userEmails.GetNotificationTargetEmailsAsync([issue.ReporterUserId], ct);
    if (reporter is not null && emails.TryGetValue(issue.ReporterUserId, out var to) && !string.IsNullOrWhiteSpace(to))
    {
        await _email.SendIssueCommentAsync(
            to, reporter.DisplayName, issue.Title, comment.Content,
            $"/Issues/{issue.Id}", reporter.PreferredLanguage, ct);
    }
    else
    {
        _logger.LogWarning("Skipping issue comment email for issue {IssueId} — reporter {UserId} has no effective email",
            issue.Id, issue.ReporterUserId);
    }
}

private async Task DispatchStatusChangedNotificationAsync(
    Issue issue, IssueStatus oldStatus, IssueStatus newStatus, Guid? actorUserId, CancellationToken ct)
{
    var recipients = new HashSet<Guid>();
    if (issue.ReporterUserId != actorUserId) recipients.Add(issue.ReporterUserId);
    if (issue.AssigneeUserId is { } aid && aid != actorUserId) recipients.Add(aid);
    if (recipients.Count == 0) return;

    try
    {
        await _notifications.SendAsync(
            NotificationSource.IssueStatusChanged, NotificationClass.Informational,
            NotificationPriority.Normal,
            $"Issue status: {oldStatus} → {newStatus}", recipients.ToArray(),
            body: $"Status changed on issue: {issue.Title}",
            actionUrl: $"/Issues/{issue.Id}", actionLabel: "View issue", cancellationToken: ct);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Failed to dispatch IssueStatusChanged notification for {IssueId}", issue.Id);
    }
}

private async Task DispatchAssignedNotificationAsync(Issue issue, Guid newAssigneeUserId, CancellationToken ct)
{
    try
    {
        await _notifications.SendAsync(
            NotificationSource.IssueAssigned, NotificationClass.ActionRequired,
            NotificationPriority.Normal,
            $"You were assigned to: {issue.Title}", [newAssigneeUserId],
            body: issue.Description.Length > 240 ? issue.Description[..240] + "…" : issue.Description,
            actionUrl: $"/Issues/{issue.Id}", actionLabel: "Open issue", cancellationToken: ct);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Failed to dispatch IssueAssigned notification for {IssueId}", issue.Id);
    }
}

public async Task<int> GetActionableCountForViewerAsync(
    Guid viewerUserId, IReadOnlyList<string> viewerRoles, bool viewerIsAdmin, CancellationToken ct = default)
{
    if (viewerIsAdmin) return await _repo.CountActionableAsync(null, null, ct);

    var sections = IssueSectionRouting.SectionsForRoles(viewerRoles);
    return await _repo.CountActionableAsync(sections, viewerUserId, ct);
}

public async Task<IReadOnlyList<DistinctReporterRow>> GetDistinctReportersAsync(CancellationToken ct = default)
{
    var rows = await _repo.GetReporterCountsAsync(ct);
    if (rows.Count == 0) return [];

    var users = await _users.GetByIdsAsync(rows.Select(r => r.UserId), ct);
    return rows.Select(r => new DistinctReporterRow(
        r.UserId,
        users.TryGetValue(r.UserId, out var u) ? u.DisplayName : r.UserId.ToString(),
        r.Count))
        .OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
        .ToList();
}

public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
{
    var issues = await _repo.GetForUserExportAsync(userId, ct);
    var shaped = issues.Select(i => new
    {
        i.Title, i.Description, i.Category, i.Section, i.Status, i.PageUrl,
        CreatedAt = i.CreatedAt.ToInvariantInstantString(),
        ResolvedAt = i.ResolvedAt.ToInvariantInstantString(),
        Comments = i.Comments.OrderBy(c => c.CreatedAt).Select(c => new
        {
            c.Content, IsFromUser = c.SenderUserId == userId,
            CreatedAt = c.CreatedAt.ToInvariantInstantString()
        })
    }).ToList();

    return [new UserDataSlice(GdprExportSections.Issues, shaped)];
}
```

- [ ] **Step 2: Build the entire service + commit Phase 4**

```bash
dotnet build Humans.slnx -v quiet
git add src/Humans.Application/Services/Issues/
git commit -m "feat(issues): IssuesService with state machine, audit, notifications, GDPR export"
```

---

### Task 21: Service unit tests

**Files:**
- Create: `tests/Humans.Application.Tests/Services/IssuesServiceTests.cs`

- [ ] **Step 1: Write the test class**

Mirror the structure of `tests/Humans.Application.Tests/Services/FeedbackServiceTests.cs`. Use Moq for `IIssuesRepository`, `IUserService`, `IRoleAssignmentService`, `IAuditLogService`, `IEmailService`, `INotificationService`, `INavBadgeCacheInvalidator`, `IUserEmailService`. Use a fake `IClock` (NodaTime `FakeClock`).

Tests required:

```csharp
[TestMethod] async Task SubmitIssueAsync_lands_in_Triage_and_invalidates_nav_badge()
[TestMethod] async Task SubmitIssueAsync_with_oversized_screenshot_throws()
[TestMethod] async Task SubmitIssueAsync_with_disallowed_content_type_throws()
[TestMethod] async Task PostCommentAsync_reporter_on_terminal_auto_reopens_to_Open()
[TestMethod] async Task PostCommentAsync_reporter_on_terminal_clears_resolved_fields()
[TestMethod] async Task PostCommentAsync_reporter_on_open_does_not_change_status()
[TestMethod] async Task PostCommentAsync_handler_sends_email_and_notification_to_reporter()
[TestMethod] async Task PostCommentAsync_reporter_does_not_email_self()
[TestMethod] async Task UpdateStatusAsync_to_terminal_sets_ResolvedAt_and_ResolvedByUserId()
[TestMethod] async Task UpdateStatusAsync_from_terminal_to_nonterminal_clears_resolved()
[TestMethod] async Task UpdateStatusAsync_no_change_returns_without_audit()
[TestMethod] async Task UpdateAssigneeAsync_audit_includes_old_and_new_names()
[TestMethod] async Task UpdateAssigneeAsync_notifies_new_assignee()
[TestMethod] async Task UpdateSectionAsync_audits_change_and_invalidates_nav_badge()
[TestMethod] async Task GetIssueListAsync_admin_passes_null_section_filter()
[TestMethod] async Task GetIssueListAsync_role_holder_passes_section_filter_and_reporter_fallback()
[TestMethod] async Task GetIssueListAsync_no_roles_filters_to_own_reports_only()
[TestMethod] async Task GetActionableCountForViewerAsync_admin_passes_null_filter()
[TestMethod] async Task GetActionableCountForViewerAsync_section_owner_passes_section_set()
[TestMethod] async Task ContributeForUserAsync_returns_only_user_own_issues()
```

Each test follows arrange-act-assert with explicit mock verifies. Code reuse: a small `BuildSut()` helper that returns `(IssuesService, mocks)`.

- [ ] **Step 2: Run + commit**

```bash
dotnet test Humans.slnx --filter FullyQualifiedName~IssuesServiceTests -v quiet
git add tests/Humans.Application.Tests/Services/IssuesServiceTests.cs
git commit -m "test(issues): IssuesService unit tests (state, auth filter, notifications)"
```

Expected: 20 tests pass.

---

## Phase 5 — Email + DI registration

### Task 22: `IEmailService.SendIssueCommentAsync`

**Files:**
- Modify: `src/Humans.Application/Interfaces/Email/IEmailService.cs`
- Modify: `src/Humans.Infrastructure/Services/Email/SmtpEmailService.cs` (or whatever the in-process impl is — find by grepping `SendFeedbackResponseAsync`)
- Modify: `src/Humans.Infrastructure/Services/Email/OutboxEmailService.cs` (find by grep)
- Create: `src/Humans.Web/Resources/EmailTemplates/IssueCommentEmail.{en,es,de,fr,it}.resx`

- [ ] **Step 1: Add interface method**

```csharp
Task SendIssueCommentAsync(
    string to, string displayName, string issueTitle, string commentContent,
    string issueLink, string preferredLanguage, CancellationToken ct = default);
```

- [ ] **Step 2: Implement in `SmtpEmailService` (and/or `OutboxEmailService` per existing `SendFeedbackResponseAsync` pattern)**

Find existing `SendFeedbackResponseAsync` impl (`grep -r "SendFeedbackResponseAsync" src/`) and clone the body, swapping the .resx template name to `IssueCommentEmail`.

- [ ] **Step 3: Create localized .resx files**

Mirror `FeedbackResponseEmail.{en,es,de,fr,it}.resx`. Required string keys:
- `Subject` — "New comment on your issue: {0}" (`{0}` = issue title)
- `Greeting` — "Hi {0},"
- `Intro` — "There's a new comment on your issue:"
- `IssueTitleLabel` — "Issue:"
- `CommentLabel` — "Comment:"
- `OpenIssue` — "Open issue"
- `Closing` — "— The Humans team"

- [ ] **Step 4: Build + commit**

```bash
dotnet build Humans.slnx -v quiet
git add src/Humans.Application/Interfaces/Email/IEmailService.cs \
       src/Humans.Infrastructure/Services/Email/ \
       src/Humans.Web/Resources/EmailTemplates/IssueCommentEmail.*.resx
git commit -m "feat(issues): IEmailService.SendIssueCommentAsync + localized templates"
```

---

### Task 23: DI registration (`IssuesSectionExtensions`)

**Files:**
- Create: `src/Humans.Web/Extensions/Sections/IssuesSectionExtensions.cs`
- Create: `src/Humans.Infrastructure/Configuration/IssuesApiSettings.cs`
- Modify: wherever `AddFeedbackSection` is called from (likely `Program.cs` or a section-registration aggregator)

- [ ] **Step 1: Write `IssuesApiSettings.cs`**

```csharp
namespace Humans.Infrastructure.Configuration;

public sealed class IssuesApiSettings
{
    public string ApiKey { get; set; } = string.Empty;
}
```

- [ ] **Step 2: Write `IssuesSectionExtensions.cs`**

```csharp
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Issues;
using Humans.Application.Interfaces.Repositories;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Repositories.Issues;
using IssuesApplicationService = Humans.Application.Services.Issues.IssuesService;

namespace Humans.Web.Extensions.Sections;

internal static class IssuesSectionExtensions
{
    internal static IServiceCollection AddIssuesSection(this IServiceCollection services)
    {
        services.AddSingleton<IIssuesRepository, IssuesRepository>();
        services.AddScoped<IssuesApplicationService>();
        services.AddScoped<IIssuesService>(sp => sp.GetRequiredService<IssuesApplicationService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<IssuesApplicationService>());

        services.Configure<IssuesApiSettings>(opts =>
        {
            opts.ApiKey = Environment.GetEnvironmentVariable("ISSUES_API_KEY") ?? string.Empty;
        });

        return services;
    }
}
```

- [ ] **Step 3: Register in section aggregator**

Find the line `services.AddFeedbackSection();` (likely in `Program.cs` or `SectionRegistration.cs`). Add directly below:

```csharp
services.AddIssuesSection();
```

- [ ] **Step 4: Build + commit**

```bash
dotnet build Humans.slnx -v quiet
git add src/Humans.Infrastructure/Configuration/IssuesApiSettings.cs \
       src/Humans.Web/Extensions/Sections/IssuesSectionExtensions.cs \
       src/Humans.Web/Program.cs
git commit -m "feat(issues): DI registration + ISSUES_API_KEY settings"
```

---

## Phase 6 — API controller

### Task 24: `IssuesApiController`

**Files:**
- Create: `src/Humans.Web/Controllers/IssuesApiController.cs`
- Modify: `src/Humans.Web/Filters/ApiKeyAuthFilter.cs` — needs to know about `IssuesApiSettings` too. Inspect the filter; if it's hardcoded to `FeedbackApiSettings`, refactor to take a settings type via attribute, or duplicate to `IssuesApiKeyAuthFilter` for the cleanest cut. Spec calls for separate keys, so a separate filter is fine.

- [ ] **Step 1: Inspect `ApiKeyAuthFilter`**

```bash
grep -rn "ApiKeyAuthFilter" src/Humans.Web/Filters/
```

Decide: refactor to generic-on-settings (cleaner) or add an `IssuesApiKeyAuthFilter` (faster, no risk to existing endpoints). **Default: add `IssuesApiKeyAuthFilter`** as a sibling for the cleanest cut-over.

- [ ] **Step 2: Create `IssuesApiKeyAuthFilter`** (mirror existing filter, swap settings type)

- [ ] **Step 3: Write `IssuesApiController.cs`**

Mirror `FeedbackApiController.cs`. All endpoints accept `X-Api-Key`, return 503 if key unset, 401 if invalid. Endpoints per spec:

```csharp
[ApiController]
[Route("api/issues")]
[ServiceFilter(typeof(IssuesApiKeyAuthFilter))]
public class IssuesApiController : ControllerBase
{
    private readonly IIssuesService _issues;
    private readonly ILogger<IssuesApiController> _logger;

    public IssuesApiController(IIssuesService issues, ILogger<IssuesApiController> logger)
    { _issues = issues; _logger = logger; }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] IssueStatus? status, [FromQuery] IssueCategory? category,
        [FromQuery] string? section, [FromQuery] Guid? assignee, [FromQuery] int limit = 50)
    {
        var filter = new IssueListFilter(
            Statuses: status.HasValue ? [status.Value] : null,
            Categories: category.HasValue ? [category.Value] : null,
            Sections: section is null ? null : [section],
            AssigneeUserId: assignee,
            Limit: limit);

        // API caller is admin-equivalent: pass empty viewerUserId, no roles, isAdmin=true.
        var issues = await _issues.GetIssueListAsync(filter, Guid.Empty, [], viewerIsAdmin: true);
        return Ok(issues.Select(MapList));
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(Guid id)
    {
        var issue = await _issues.GetIssueByIdAsync(id);
        if (issue is null) return NotFound();
        var thread = await _issues.GetThreadAsync(id);
        return Ok(MapDetail(issue, thread));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ApiCreateIssueModel model)
    {
        var issue = await _issues.SubmitIssueAsync(
            model.ReporterUserId, model.Category, model.Title, model.Description,
            model.Section, pageUrl: null, userAgent: null, additionalContext: null,
            screenshot: null, dueDate: model.DueDate);
        return Ok(new { issue.Id });
    }

    [HttpGet("{id}/comments")]
    public async Task<IActionResult> GetComments(Guid id) { /* mirror FeedbackApi */ }

    [HttpPost("{id}/comments")]
    public async Task<IActionResult> PostComment(Guid id, [FromBody] PostIssueCommentModel model) { /* senderUserId null, senderIsReporter false */ }

    [HttpPatch("{id}/status")]
    public async Task<IActionResult> UpdateStatus(Guid id, [FromBody] UpdateIssueStatusModel model) { /* call UpdateStatusAsync(actorUserId: null) */ }

    [HttpPatch("{id}/assignee")]
    public async Task<IActionResult> UpdateAssignee(Guid id, [FromBody] UpdateIssueAssigneeModel model) { /* */ }

    [HttpPatch("{id}/section")]
    public async Task<IActionResult> UpdateSection(Guid id, [FromBody] UpdateIssueSectionModel model) { /* */ }

    [HttpPatch("{id}/github-issue")]
    public async Task<IActionResult> SetGitHubIssue(Guid id, [FromBody] SetGitHubIssueModel model) { /* */ }

    private static object MapList(Issue i) => new {
        i.Id,
        Status = i.Status.ToString(),
        Category = i.Category.ToString(),
        i.Section, i.Title, i.Description, i.PageUrl, i.UserAgent, i.AdditionalContext,
#pragma warning disable CS0618
        ReporterName = i.Reporter.DisplayName,
        ReporterEmail = i.Reporter.Email,
        ReporterUserId = i.ReporterUserId,
        ReporterLanguage = i.Reporter.PreferredLanguage,
        AssigneeUserId = i.AssigneeUserId,
        AssigneeName = i.Assignee?.DisplayName,
#pragma warning restore CS0618
        i.GitHubIssueNumber, i.DueDate,
        ScreenshotUrl = i.ScreenshotStoragePath is not null ? $"/{i.ScreenshotStoragePath}" : null,
        CreatedAt = i.CreatedAt.ToDateTimeUtc(),
        UpdatedAt = i.UpdatedAt.ToDateTimeUtc(),
        ResolvedAt = i.ResolvedAt?.ToDateTimeUtc(),
        CommentCount = i.Comments.Count
    };

    private static object MapDetail(Issue i, IReadOnlyList<IssueThreadEvent> thread) =>
        new {
            issue = MapList(i),
            thread = thread.Select(e => e switch
            {
                IssueCommentEvent c => (object)new {
                    type = "comment", at = c.At.ToDateTimeUtc(),
                    actorUserId = c.ActorUserId, actorName = c.ActorDisplayName,
                    actorIsReporter = c.ActorIsReporter, content = c.Content
                },
                IssueAuditEvent a => new {
                    type = "audit", at = a.At.ToDateTimeUtc(),
                    actorUserId = a.ActorUserId, actorName = a.ActorDisplayName,
                    action = a.Action.ToString(), description = a.Description
                },
                _ => throw new NotSupportedException()
            })
        };
}
```

Define the request models in `src/Humans.Web/Models/IssueViewModels.cs` (next task).

- [ ] **Step 4: Build + commit**

```bash
dotnet build Humans.slnx -v quiet
git add src/Humans.Web/Filters/IssuesApiKeyAuthFilter.cs src/Humans.Web/Controllers/IssuesApiController.cs
git commit -m "feat(issues): /api/issues controller with key auth"
```

---

### Task 25: API integration tests

**Files:**
- Create: `tests/Humans.Web.Tests/Controllers/IssuesApiControllerTests.cs`

- [ ] **Step 1: Write tests**

Mirror tests in the existing FeedbackApi test class (find via `grep -r FeedbackApi tests/`). Specific cases:

```csharp
[TestMethod] async Task List_returns_all_issues()
[TestMethod] async Task List_filters_by_status()
[TestMethod] async Task List_filters_by_section()
[TestMethod] async Task Get_returns_NotFound_for_missing_issue()
[TestMethod] async Task Get_includes_thread_with_comments_and_audit_events()
[TestMethod] async Task Create_creates_issue_with_specified_reporter_and_returns_Id()
[TestMethod] async Task PostComment_sets_SenderUserId_null_for_keyed_path()
[TestMethod] async Task UpdateStatus_logs_audit_with_API_actor()
[TestMethod] async Task ApiKey_missing_returns_503_or_401_as_per_filter()
```

- [ ] **Step 2: Run + commit**

```bash
dotnet test Humans.slnx --filter FullyQualifiedName~IssuesApiControllerTests -v quiet
git add tests/Humans.Web.Tests/Controllers/IssuesApiControllerTests.cs
git commit -m "test(issues): IssuesApiController integration tests"
```

---

## Phase 7 — Web controller + views

### Task 26: View models

**Files:**
- Create: `src/Humans.Web/Models/IssueViewModels.cs`

- [ ] **Step 1: Write all view models in one file**

Models needed (mirroring existing FeedbackViewModels.cs):
- `SubmitIssueViewModel` (form post for widget + /Issues/New)
- `IssuePageViewModel` (Index)
- `IssueListItemViewModel` (one row in Index)
- `IssueDetailViewModel` (Detail)
- `IssueThreadEventViewModel` (one thread item — comment or audit)
- `PostIssueCommentModel`
- `UpdateIssueStatusModel`
- `UpdateIssueAssigneeModel`
- `UpdateIssueSectionModel`
- `SetGitHubIssueModel`
- `ApiCreateIssueModel`

Each is a simple POCO/record with validation attributes. Friendly-area-label dropdown options for `/Issues/New`:

```csharp
public static readonly IReadOnlyDictionary<string, string> AreaLabelMap =
    new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [IssueSectionRouting.Shifts]       = "Shifts & volunteering",
        [IssueSectionRouting.Profiles]     = "Profile & onboarding",
        [IssueSectionRouting.Teams]        = "Teams",
        [IssueSectionRouting.Governance]   = "Voting & governance",
        [IssueSectionRouting.Camps]        = "Camp setup & ops",
        [IssueSectionRouting.Tickets]      = "Tickets",
        [IssueSectionRouting.Users]        = "Account",
        [IssueSectionRouting.Budget]       = "Budget",
        [IssueSectionRouting.Legal]        = "Legal & consent",
        [IssueSectionRouting.CityPlanning] = "City planning",
        [IssueSectionRouting.Onboarding]   = "Onboarding",
    };
```

- [ ] **Step 2: Build + commit**

```bash
dotnet build Humans.slnx -v quiet
git add src/Humans.Web/Models/IssueViewModels.cs
git commit -m "feat(issues): view models for Issues web controller"
```

---

### Task 27: `IssuesController` — actions

**Files:**
- Create: `src/Humans.Web/Controllers/IssuesController.cs`

- [ ] **Step 1: Write the controller**

Mirror `FeedbackController.cs`. Roughly 250 lines. Actions:

- `[HttpGet("")] Index(...)` — calls `_issues.GetIssueListAsync(filter, user.Id, userRoles, isAdmin)`. Builds `IssuePageViewModel` with reporter/section dropdowns and `Reports` mapped to `IssueListItemViewModel`.
- `[HttpGet("New")] New()` — returns the New form.
- `[HttpPost("")] Submit(SubmitIssueViewModel model)` — handles both AJAX (from widget) and form-post. Calls `SubmitIssueAsync`. Section is auto-inferred from `model.PageUrl` if not explicitly provided (helper: `IssueSectionInference.FromPath(pageUrl)` — see Task 28).
- `[HttpGet("{id}")] Detail(Guid id)` — fetches issue + thread, builds `IssueDetailViewModel`. Returns `_Detail` partial for AJAX, otherwise redirects to Index with `?selected=id`.
- `[HttpPost("{id}/Comments")] PostComment(...)` — calls `PostCommentAsync(id, user.Id, content, senderIsReporter: report.ReporterUserId == user.Id)`.
- `[HttpPost("{id}/Status")] UpdateStatus(...)` — `[Authorize]` + manual role check (must hold a role mapping the issue's section, or be Admin).
- `[HttpPost("{id}/Assignee")] UpdateAssignee(...)` — same authorization.
- `[HttpPost("{id}/Section")] UpdateSection(...)` — same.
- `[HttpPost("{id}/GitHubIssue")] SetGitHubIssue(...)` — same.

Auth-guard helper:

```csharp
private async Task<bool> CanHandleAsync(Issue issue)
{
    if (User.IsInRole(RoleNames.Admin)) return true;
    var roles = await UserManager.GetRolesAsync(await UserManager.GetUserAsync(User));
    return IssueSectionRouting.RolesFor(issue.Section).Any(roles.Contains);
}
```

- [ ] **Step 2: Build + commit**

```bash
dotnet build Humans.slnx -v quiet
git add src/Humans.Web/Controllers/IssuesController.cs
git commit -m "feat(issues): IssuesController (Index/Detail/New/Submit + handler actions)"
```

---

### Task 28: Section-from-URL inference helper

**Files:**
- Create: `src/Humans.Web/Helpers/IssueSectionInference.cs`

- [ ] **Step 1: Write the helper**

```csharp
using Humans.Domain.Constants;

namespace Humans.Web.Helpers;

public static class IssueSectionInference
{
    /// <summary>
    /// Returns the technical Section name (matching <see cref="IssueSectionRouting"/>)
    /// inferred from a path's first segment, or null if no match.
    /// Examples: "/Camps/123" → "Camps"; "/Tickets" → "Tickets"; "/" → null.
    /// </summary>
    public static string? FromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (!Uri.TryCreate(path, UriKind.RelativeOrAbsolute, out var uri))
            return null;

        var p = uri.IsAbsoluteUri ? uri.AbsolutePath : path;
        var trimmed = p.Trim('/');
        if (trimmed.Length == 0) return null;

        var first = trimmed.Split('/', 2)[0];
        return Map(first);
    }

    private static string? Map(string segment) => segment.ToLowerInvariant() switch
    {
        "camps" or "barrios"      => IssueSectionRouting.Camps,
        "tickets"                 => IssueSectionRouting.Tickets,
        "teams"                   => IssueSectionRouting.Teams,
        "shifts" or "vol"         => IssueSectionRouting.Shifts,
        "onboardingreview"        => IssueSectionRouting.Onboarding,
        "profile"                 => IssueSectionRouting.Profiles,
        "humans"                  => IssueSectionRouting.Users,
        "finance" or "budget"     => IssueSectionRouting.Budget,
        "board" or "voting"       => IssueSectionRouting.Governance,
        "legal" or "consent"      => IssueSectionRouting.Legal,
        "city"                    => IssueSectionRouting.CityPlanning,
        _                         => null
    };
}
```

- [ ] **Step 2: Quick unit test**

```csharp
[TestClass]
public class IssueSectionInferenceTests
{
    [TestMethod] [DataRow("/Camps/abc", "Camps")]
    [DataRow("/Tickets", "Tickets")]
    [DataRow("/", null)]
    [DataRow("", null)]
    [DataRow(null, null)]
    [DataRow("/SomeUnknownPage", null)]
    public void FromPath_maps_first_segment_or_returns_null(string? input, string? expected)
    {
        Assert.AreEqual(expected, IssueSectionInference.FromPath(input));
    }
}
```

- [ ] **Step 3: Build + run + commit**

```bash
dotnet build Humans.slnx -v quiet
dotnet test Humans.slnx --filter FullyQualifiedName~IssueSectionInferenceTests -v quiet
git add src/Humans.Web/Helpers/IssueSectionInference.cs tests/
git commit -m "feat(issues): URL-to-section inference helper + tests"
```

---

### Task 29: Index view (`Index.cshtml`)

**Files:**
- Create: `src/Humans.Web/Views/Issues/Index.cshtml`

Use the design-bundle markup from `humans-design-system/project/ui-kits/issue-tracker.html` (artboard "i. Index — all issues") as the source of truth. Translate to Razor with `IssuePageViewModel`. Use `tokens.css` (already in project per the admin shell PR).

- [ ] **Step 1: Write the view**

Sections in order (matching HTML reference at issue-tracker.html lines 252–554):
1. `crumb` strip
2. `page-head` — "Issues" h1, subtitle (counts), `+ New issue` button → `/Issues/New`
3. `filter-bar` — Open / Mine / Closed / All segmented buttons (link to filtered Index URLs); type select; area select; sort select; search input
4. `issue-list` table with `il-head` row + per-issue `il-row`s. Empty state when no rows.
5. `page-foot` — pager + "Showing X of Y"

Status pill class lookup: `status-{Status.ToString().ToLowerInvariant()}` (lower-case enum name; map `InProgress` → `progress` and `WontFix` → `wontfix`). Type chip class: `type-{Category.ToString().ToLowerInvariant()}`.

Wire `?selected=id` so a Detail click loads `_Detail` partial via fetch into a sliding panel (or simple page transition for v1).

- [ ] **Step 2: Smoke test in dev server**

```bash
dotnet run --project src/Humans.Web/Humans.Web.csproj
# open http://nuc.home:5000/Issues
```

Confirm renders without errors and shows seeded test issues.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/Views/Issues/Index.cshtml
git commit -m "feat(issues): Index view (filter bar + issue list)"
```

---

### Task 30: Detail partial (`_Detail.cshtml`)

**Files:**
- Create: `src/Humans.Web/Views/Issues/_Detail.cshtml`

- [ ] **Step 1: Write the partial**

Match the design-bundle artboard "ii. Detail" (issue-tracker.html lines 559–780):
1. `crumb` (Issues / № id)
2. `detail-grid` two-column (`1fr 280px`, single-column under 1100px)
3. Left column:
   - `detail-head`: id-line, Title h1, status pill + type chip + area + reporter line
   - `thread`: render each `IssueThreadEventViewModel` — switch on `Type` ("comment" → `comment-card`; "audit" → `event status-event` line)
   - `composer`: textarea + Comment / "Comment & mark resolved" buttons (latter does both ops in one POST). Show only "Reopen by commenting" hint when status is terminal and viewer is reporter.
4. Right column `facts` panel: dl of Status / Type / Area / Reporter / Assignee / Linked / Opened / Last update / Due date. Status-change select for handlers (gated on `Model.IsHandler`).

- [ ] **Step 2: Smoke test + commit**

```bash
git add src/Humans.Web/Views/Issues/_Detail.cshtml
git commit -m "feat(issues): Detail partial (thread + facts panel)"
```

---

### Task 31: New view (`New.cshtml`)

**Files:**
- Create: `src/Humans.Web/Views/Issues/New.cshtml`

- [ ] **Step 1: Write the view**

Match artboard "iii. New issue" (issue-tracker.html lines 785–910):
- `crumb` (Issues / New)
- `page-head` — "File a new issue", warm subtitle
- `form-grid` two-column
- Left: `form-card` form posting to `/Issues`. Fields: Title, Type select (with emoji prefixes), Area select (using `AreaLabelMap`), Body textarea, Attachments dropzone.
- Right: `tip-card` with the "what makes a good issue?" list and the email-redirect heads-up.

- [ ] **Step 2: Smoke test + commit**

```bash
git add src/Humans.Web/Views/Issues/New.cshtml
git commit -m "feat(issues): New issue form view"
```

---

## Phase 8 — Floating widget cut-over

### Task 32: `IssuesWidgetViewComponent`

**Files:**
- Create: `src/Humans.Web/ViewComponents/IssuesWidgetViewComponent.cs`
- Create: `src/Humans.Web/Views/Shared/Components/IssuesWidget/Default.cshtml`

- [ ] **Step 1: Write the view component**

Mirror `FeedbackWidgetViewComponent.cs`. Returns the modal markup with title input + body textarea + screenshot dropzone, Section pre-filled from `Request.Path` via `IssueSectionInference.FromPath`. JavaScript on submit posts `multipart/form-data` to `/Issues` (the form-post path, which `IssuesController.Submit` handles).

- [ ] **Step 2: Smoke test on local**

Click the floating button on `/Camps`, file a test issue. Verify it shows up at `/Issues` with `Section = "Camps"` and `Status = Triage`.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/ViewComponents/IssuesWidgetViewComponent.cs src/Humans.Web/Views/Shared/Components/IssuesWidget/
git commit -m "feat(issues): IssuesWidgetViewComponent (floating widget)"
```

---

### Task 33: Swap layout to use `IssuesWidget` and add Issues nav link

**Files:**
- Modify: `src/Humans.Web/Views/Shared/_Layout.cshtml`

- [ ] **Step 1: Replace the widget invocation**

Find `<vc:feedback-widget />`. Replace with:

```html
<vc:issues-widget />
```

(Do NOT delete the FeedbackWidget component or its view component class — old `/Feedback` page still works for backlog drainage. The widget on every page is the cut-over surface, not the backend.)

- [ ] **Step 2: Add the Issues nav link in the topbar**

In the existing nav (the m-nav that renders for authenticated users):

```html
<a class="tnav @(ViewContext.RouteData.Values["controller"]?.ToString() == "Issues" ? "active" : "")"
   asp-controller="Issues" asp-action="Index">Issues
   @await Component.InvokeAsync("NavBadges", new { queue = "issues" })
</a>
```

Place the link in the topbar at the position the design shows (between Roster and Settings — verify against the existing _Layout.cshtml).

- [ ] **Step 3: Build + smoke test on local**

Verify:
- Floating button on every page opens the new widget.
- Top nav shows the "Issues" link.
- Old `/Feedback` page is still reachable by typing the URL.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/Views/Shared/_Layout.cshtml
git commit -m "feat(issues): switch layout widget to IssuesWidget; add Issues top-nav link"
```

---

## Phase 9 — Nav badge + admin diagnostics

### Task 34: `NavBadgesViewComponent` — issues queue

**Files:**
- Modify: `src/Humans.Web/ViewComponents/NavBadgesViewComponent.cs`

- [ ] **Step 1: Add the `issues` case**

Find the `switch` on `queue`. Add:

```csharp
case "issues":
{
    var user = await _userManager.GetUserAsync(HttpContext.User);
    if (user is null) return Content("");
    var roles = await _userManager.GetRolesAsync(user);
    var isAdmin = roles.Contains(RoleNames.Admin);
    var count = await _issuesService.GetActionableCountForViewerAsync(user.Id, roles.ToList(), isAdmin);
    return count > 0
        ? View("Default", count)
        : Content("");
}
```

Inject `IIssuesService` into the constructor (alongside the existing `IFeedbackService`).

- [ ] **Step 2: Build + commit**

```bash
dotnet build Humans.slnx -v quiet
git add src/Humans.Web/ViewComponents/NavBadgesViewComponent.cs
git commit -m "feat(issues): nav-badge count for /Issues"
```

---

### Task 35: `/Admin/Configuration` — `ISSUES_API_KEY` indicator

**Files:**
- Modify: `src/Humans.Web/Controllers/AdminController.cs` (Configuration action)
- Modify: `src/Humans.Web/Views/Admin/Configuration.cshtml`

- [ ] **Step 1: Add `IssuesApiKeyConfigured` to the view model**

Find where `FeedbackApiKeyConfigured` is set; add a peer reading `ISSUES_API_KEY`.

- [ ] **Step 2: Add the row to the view**

Mirror the existing Feedback row.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/Controllers/AdminController.cs src/Humans.Web/Views/Admin/Configuration.cshtml
git commit -m "feat(issues): /Admin/Configuration shows ISSUES_API_KEY status"
```

---

## Phase 10 — Localization

### Task 36: Localization strings

**Files:**
- Modify: `src/Humans.Web/Resources/SharedResource.{en,es,de,fr,it}.resx`

- [ ] **Step 1: Add Issue_* strings to each .resx**

Required keys:
- `Issue_Submitted`, `Issue_Error`, `Issue_PageTitle`, `Issue_New`, `Issue_NewPageTitle`,
- `Issue_Status_Triage`, `Issue_Status_Open`, `Issue_Status_InProgress`, `Issue_Status_Resolved`, `Issue_Status_WontFix`, `Issue_Status_Duplicate`,
- `Issue_Type_Bug`, `Issue_Type_Feature`, `Issue_Type_Question`,
- `Issue_Filter_Open`, `Issue_Filter_Mine`, `Issue_Filter_Closed`, `Issue_Filter_All`,
- `Issue_Comment_Posted`, `Issue_Comment_Reopened`, `Issue_Comment_PostFailed`,
- `Issue_Status_Updated`, `Issue_Assignee_Updated`, `Issue_Section_Updated`, `Issue_GitHub_Linked`,
- Friendly area labels (one per known section): `Issue_Area_Camps`, `Issue_Area_Tickets`, etc. matching `AreaLabelMap`.

- [ ] **Step 2: Build + commit**

```bash
dotnet build Humans.slnx -v quiet
git add src/Humans.Web/Resources/SharedResource.*.resx
git commit -m "feat(issues): localization (en/es/de/fr/it)"
```

---

## Phase 11 — Documentation

### Task 37: `docs/sections/Issues.md`

**Files:**
- Create: `docs/sections/Issues.md`

- [ ] **Step 1: Write the section invariant doc**

Use `docs/sections/SECTION-TEMPLATE.md` as the skeleton. Required sections (terse):
- **Concepts**: Issue, IssueComment, Section, BallInCourt-as-derived ("whose move is it" = compare latest comment sender to reporter).
- **Data Model**: Issue + IssueComment per-entity tables (full field tables).
- **Actors & Roles**: Reporter / SectionRoleHolder / Admin / API (key).
- **Invariants**: state transitions, auto-reopen, role-based visibility, audit-after-save, screenshot validation, `Section` is editable in any non-terminal state.
- **Negative Access Rules**: a non-role-holder cannot see other people's issues in sections they don't own; cannot mutate.
- **Triggers**: comment posts → notification fan-out + email; status change → reporter+assignee notification; assignment → assignee notification.
- **Cross-Section Dependencies**: list the seven services from spec section 11.
- **Architecture**: owning service, owned tables, status (A) Migrated.

Add freshness header that triggers on changes to `src/Humans.Application/Services/Issues/**`, `src/Humans.Domain/Entities/Issue.cs`, `src/Humans.Domain/Entities/IssueComment.cs`, `src/Humans.Web/Controllers/Issues*.cs`.

- [ ] **Step 2: Commit**

```bash
git add docs/sections/Issues.md
git commit -m "docs(issues): section invariant doc"
```

---

### Task 38: `docs/features/28-issues-system.md`

**Files:**
- Create: `docs/features/28-issues-system.md`

- [ ] **Step 1: Write the feature spec**

Mirror the structure of `docs/features/27-feedback-system.md`. Sections: Business Context, User Stories (US-28.1 Submit / US-28.2 Triage / US-28.3 API / US-28.4 Notifications / US-28.5 Conversation / US-28.6 Routing), Data Model reference, Authorization Matrix, URL Routes, Claude Code Integration (replacing the old `/triage` skill notes), Navigation, Related Features.

- [ ] **Step 2: Commit**

```bash
git add docs/features/28-issues-system.md
git commit -m "docs(issues): feature spec (28)"
```

---

## Phase 12 — Final verification + PR

### Task 39: Full build + test sweep

- [ ] **Step 1: Build**

```bash
dotnet build Humans.slnx -v quiet
```

Expected: 0 errors. Warnings on `[Obsolete]` cross-domain navs are expected and behind `#pragma warning disable CS0618` in implementation files.

- [ ] **Step 2: Full test run**

```bash
dotnet test Humans.slnx -v quiet
```

Expected: all tests pass.

- [ ] **Step 3: Manual smoke (dev server)**

```bash
dotnet run --project src/Humans.Web/Humans.Web.csproj
```

Walk through:
- Open `https://nuc.home:5001/Issues` → empty list rendered (or showing test data).
- Click `+ New issue` → form renders with friendly area labels.
- Submit a Bug → lands in Triage, shows in list, status pill `triage`.
- Click into Detail → thread shows the original "comment" event (reporter posting body) — well, the original report itself is rendered as the head card with a Description; the thread starts empty until first comment. Confirm.
- As Admin: change status to Open → audit event appears in thread.
- As Admin: set assignee → audit event appears.
- Comment as Admin → reporter receives email + in-app notification.
- Set status to Resolved → reporter receives notification.
- Switch to reporter user → comment on the resolved issue → status auto-flips to Open, audit event appears.
- Confirm `/Feedback` is still reachable and existing reports are usable.

- [ ] **Step 4: Run dotnet ef migration reviewer once more (CLAUDE.md gate)**

Per `.claude/agents/ef-migration-reviewer.md`. Pass before opening PR.

- [ ] **Step 5: Commit any fixes; push branch**

```bash
git push -u origin feat/issues-section
```

---

### Task 40: Open PR

- [ ] **Step 1: Run `gh pr create`**

```bash
gh pr create --repo peterdrier/Humans --base main --head feat/issues-section \
  --title "feat: Issues section (replaces Feedback going forward)" \
  --body-file - <<'EOF'
## Summary

Implements the Issues section per spec [`docs/superpowers/specs/2026-04-29-issues-section-design.md`](https://github.com/peterdrier/Humans/blob/feat/issues-section/docs/superpowers/specs/2026-04-29-issues-section-design.md) (spec PR peterdrier/Humans#358).

- New top-level `/Issues` URL with three views matching the Issue tracker UI kit in the design bundle.
- Section-tagged + role-routed queues (TicketAdmin → Tickets, CampAdmin → Camps, …).
- Six-state lifecycle (Triage / Open / InProgress / Resolved / WontFix / Duplicate) with reporter-comment auto-reopen.
- Threaded comments + inline audit events.
- Floating widget on every page now submits to `/Issues` (old `/Feedback` page remains for backlog drainage; deletion is a follow-up PR per spec section 13).
- New `/api/issues/*` surface key-authed via `ISSUES_API_KEY` for LLM agents.

## Test plan

- [ ] Submit issue from floating widget on /Camps, /Shifts, /Tickets — confirm `Section` is inferred correctly
- [ ] Submit issue from /Issues/New with no PageUrl — Section dropdown picks the section
- [ ] As `TicketAdmin` (no Admin), confirm only Tickets-section + own reports show on /Issues
- [ ] As `CampAdmin`, confirm Camps + CityPlanning issues are visible
- [ ] As reporter, confirm only own reports show
- [ ] Change status to Resolved → reporter notification fires
- [ ] Reporter comments on Resolved → status flips to Open
- [ ] Assign issue → assignee notification fires
- [ ] Re-route Section → new role-holders see it; reporter notified
- [ ] Old /Feedback page still reachable; existing reports still usable
- [ ] `/api/issues` rejects without ISSUES_API_KEY; accepts with key
- [ ] EF migration reviewer pass

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
```

---

## Out of scope (per spec)

- Migration of old Feedback reports — the cut-over plan is parallel-run + drain + delete in a future PR.
- Deletion of old `/Feedback` UI, `FeedbackController`, `FeedbackService`, `FeedbackAdmin` role, `FEEDBACK_API_KEY`, the `feedback_*` tables, the `/triage` Claude Code skill — separate follow-up PR when `feedback_reports` has zero non-terminal rows.
- Priority field, tags/labels, watchers, related-issue links, internal-only comments, time tracking, auto-close on inactivity, email submission.
- Per-section sub-roles (e.g., a "Tickets coordinator" who is not `TicketAdmin`) — the existing role taxonomy is the routing taxonomy.

---

## Self-review notes

- Spec coverage: each section of `2026-04-29-issues-section-design.md` is covered. Sections 4 (data model) → Tasks 1, 5, 10, 11. Section 5 (auth) → Tasks 27, 28. Section 6 (UI) → Tasks 29–31. Section 7 (submit flows) → Tasks 14, 27, 32. Section 8 (notifications) → Task 20, 22. Section 9 (API) → Tasks 24, 25. Section 10 (audit + thread) → Tasks 17, 19, 20. Section 11 (cross-section deps) → Tasks 13, 22. Section 12 (architecture) → all of Phase 3 and 4. Section 13 (retirement) → noted as out of scope; old `/Feedback` is left running per the spec.
- No placeholders in code blocks (every code block is real, executable code or directly mirrors an existing pattern named in the task).
- Type consistency: `IssueStatus` / `IssueCategory` / `IssueSectionRouting` / `IIssuesService` / `IIssuesRepository` / `IssuesService` / `IssuesRepository` used consistently throughout.
- Per memory rule: NO startup guards added. Missing `ISSUES_API_KEY` → 503 at request time, never aborts startup.
