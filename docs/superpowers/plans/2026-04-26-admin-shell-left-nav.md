# Admin Shell + Left Nav — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the nine dark-orange admin items in the top nav with a single `Admin` link to a new `/Admin` shell that has a role-aware left sidebar, adopting the `humans-design-system` Renaissance aesthetic for the admin area only.

**Architecture:** New `_AdminLayout.cshtml` master with `body.admin-shell` scope. `AdminSidebar` ViewComponent reads a static configured nav tree, runs `IAuthorizationService` per item, hides items the user can't see and groups that become empty. `AdminBreadcrumb` ViewComponent reverse-looks-up the active item. `Admin/Index` returns a skeleton dashboard backed by real data where cheap (active-human count, shift coverage %, open feedback, system health, recent audit log). Admin-only view folders pick up `_AdminLayout` via `_ViewStart.cshtml`; `Profile/` is mixed and the six admin-side views set `Layout` per-view. Top-nav surgery is the last step so the admin shell is verifiable in isolation before the navbar changes.

**Tech Stack:** ASP.NET Core 10 MVC, Razor views, Bootstrap 5.3 (already loaded — offcanvas is the mobile-sidebar mechanism, no new JS), xUnit for ViewComponent unit tests, NodaTime for any timestamp work.

**Reference:** `docs/superpowers/specs/2026-04-26-admin-shell-left-nav-design.md` (the design spec).

---

## Phase 1 — Foundations (no behavior change)

### Task 1: Drop tokens.css and create empty admin-shell.css

**Files:**
- Create: `src/Humans.Web/wwwroot/css/tokens.css` (copied from `humans-design-system/project/tokens.css` in the design bundle)
- Create: `src/Humans.Web/wwwroot/css/admin-shell.css`

- [ ] **Step 1: Copy tokens.css from design bundle**

```bash
cp /tmp/design-fetch/humans-design-system/project/tokens.css \
   src/Humans.Web/wwwroot/css/tokens.css
```

(If the extracted bundle isn't on disk, re-fetch from `https://api.anthropic.com/v1/design/h/tEGeOaNlIrMTa6ZJy7XG9A` — it's a gzipped tar of `humans-design-system/`.)

- [ ] **Step 2: Create empty admin-shell.css with scope marker comment**

```css
/* admin-shell.css — Renaissance admin console styles.
   ALL rules in this file MUST be scoped under body.admin-shell so they
   never bleed into member pages that use _Layout.cshtml. */

/* Phase 4 fills this file with sidebar/breadcrumb/page-head/dashboard/offcanvas styles. */
```

- [ ] **Step 3: Build to confirm nothing breaks**

```bash
dotnet build Humans.slnx -v quiet
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/wwwroot/css/tokens.css src/Humans.Web/wwwroot/css/admin-shell.css
git commit -m "feat(admin-shell): add tokens.css and empty admin-shell.css"
```

---

### Task 2: Define `AdminNavGroup` / `AdminNavItem` records

**Files:**
- Create: `src/Humans.Web/ViewComponents/AdminNav.cs`

- [ ] **Step 1: Create the record file**

```csharp
using System.Security.Claims;
using Humans.Web.Authorization;

namespace Humans.Web.ViewComponents;

/// <summary>
/// A group of related admin sidebar items, rendered under an italic gold-tinted h4
/// divider. Groups whose items are all hidden by authorization disappear entirely.
/// </summary>
public sealed record AdminNavGroup(string LabelKey, IReadOnlyList<AdminNavItem> Items);

/// <summary>
/// A single sidebar entry. Exactly one of (Controller+Action) or RawHref is set.
/// Policy is preferred over RoleCheck — the latter exists for items that can't be
/// expressed as a single policy.
/// </summary>
public sealed record AdminNavItem(
    string LabelKey,
    string? Controller,
    string? Action,
    object? RouteValues,
    string? RawHref,
    string IconCssClass,
    string? Policy,
    Func<ClaimsPrincipal, bool>? RoleCheck = null,
    Func<IServiceProvider, ValueTask<int?>>? PillCount = null,
    Func<IWebHostEnvironment, bool>? EnvironmentGate = null);
```

- [ ] **Step 2: Build**

```bash
dotnet build Humans.slnx -v quiet
```

Expected: 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/ViewComponents/AdminNav.cs
git commit -m "feat(admin-shell): add AdminNavGroup and AdminNavItem records"
```

---

### Task 3: Define the configured admin nav tree

**Files:**
- Create: `src/Humans.Web/ViewComponents/AdminNavTree.cs`

- [ ] **Step 1: Create the static configured tree**

```csharp
using Humans.Domain.Constants;
using Humans.Web.Authorization;

namespace Humans.Web.ViewComponents;

/// <summary>
/// The configured admin sidebar tree. Order is by daily-traffic-across-the-whole-
/// admin-audience, NOT by structural prominence (so Voting/Review do NOT appear at
/// the top). See feedback memory: voting/review serve ~8 people, not the 800
/// humans on the platform.
/// </summary>
public static class AdminNavTree
{
    public static IReadOnlyList<AdminNavGroup> Groups { get; } = new AdminNavGroup[]
    {
        new("AdminGroup_Operations", new AdminNavItem[]
        {
            new("AdminNav_Volunteers", "Vol", "Index",       null, null, "fa-solid fa-people-group",     PolicyNames.VolunteerSectionAccess),
            new("AdminNav_Tickets",    "Ticket", "Index",    null, null, "fa-solid fa-ticket",            PolicyNames.TicketAdminBoardOrAdmin),
            new("AdminNav_Scanner",    "Scanner", "Index",   null, null, "fa-solid fa-qrcode",            PolicyNames.TicketAdminBoardOrAdmin),
        }),
        new("AdminGroup_Members", new AdminNavItem[]
        {
            new("AdminNav_Humans", "Profile", "AdminList",       null, null, "fa-solid fa-users",            PolicyNames.HumanAdminBoardOrAdmin),
            new("AdminNav_Review", "OnboardingReview", "Index",   null, null, "fa-solid fa-clipboard-check",  PolicyNames.ReviewQueueAccess,
                 PillCount: PillCounts.ReviewQueue),
        }),
        new("AdminGroup_Money", new AdminNavItem[]
        {
            new("AdminNav_Finance", "Finance", "Index", null, null, "fa-solid fa-coins", PolicyNames.FinanceAdminOrAdmin),
        }),
        new("AdminGroup_Governance", new AdminNavItem[]
        {
            new("AdminNav_Voting", "OnboardingReview", "BoardVoting", null, null, "fa-solid fa-check-to-slot", PolicyNames.BoardOrAdmin,
                 PillCount: PillCounts.VotingQueue),
            new("AdminNav_Board",  "Board", "Index",                  null, null, "fa-solid fa-gavel",          PolicyNames.BoardOrAdmin),
        }),
        new("AdminGroup_Integrations", new AdminNavItem[]
        {
            new("AdminNav_Google",            "Google", "Index",        null, null, "fa-brands fa-google",   PolicyNames.AdminOnly),
            new("AdminNav_EmailPreview",      "Email",  "EmailPreview", null, null, "fa-solid fa-envelope",  PolicyNames.AdminOnly),
            new("AdminNav_EmailOutbox",       "Email",  "EmailOutbox",  null, null, "fa-solid fa-inbox",     PolicyNames.AdminOnly),
            new("AdminNav_Campaigns",         "Campaign", "Index",      null, null, "fa-solid fa-bullhorn",  PolicyNames.AdminOnly),
            new("AdminNav_WorkspaceAccounts", "Google",  "Accounts",    null, null, "fa-solid fa-at",        PolicyNames.AdminOnly),
        }),
        new("AdminGroup_PeopleData", new AdminNavItem[]
        {
            new("AdminNav_Merge",          "AdminMerge", "Index",            null, null, "fa-solid fa-code-merge", PolicyNames.AdminOnly),
            new("AdminNav_Duplicates",     "AdminDuplicateAccounts", "Index", null, null, "fa-solid fa-clone",      PolicyNames.AdminOnly),
            new("AdminNav_Audience",       "Admin", "AudienceSegmentation",   null, null, "fa-solid fa-chart-pie",  PolicyNames.AdminOnly),
            new("AdminNav_LegalDocuments", "AdminLegalDocuments", "Index",    null, null, "fa-solid fa-scale-balanced", PolicyNames.AdminOnly),
        }),
        new("AdminGroup_Diagnostics", new AdminNavItem[]
        {
            new("AdminNav_Logs",          "Admin", "Logs",          null, null, "fa-solid fa-triangle-exclamation", PolicyNames.AdminOnly),
            new("AdminNav_DbStats",       "Admin", "DbStats",       null, null, "fa-solid fa-database",            PolicyNames.AdminOnly),
            new("AdminNav_CacheStats",    "Admin", "CacheStats",    null, null, "fa-solid fa-bolt",                PolicyNames.AdminOnly),
            new("AdminNav_Configuration", "Admin", "Configuration", null, null, "fa-solid fa-gear",                PolicyNames.AdminOnly),
            new("AdminNav_Hangfire",      null, null, null, "/hangfire",      "fa-solid fa-clock-rotate-left", PolicyNames.AdminOnly),
            new("AdminNav_Health",        null, null, null, "/health/ready",  "fa-solid fa-heart-pulse",       PolicyNames.AdminOnly),
        }),
        new("AdminGroup_Dev", new AdminNavItem[]
        {
            new("AdminNav_SeedBudget",    "DevSeed", "SeedBudget",    null, null, "fa-solid fa-coins",     PolicyNames.AdminOnly,
                 EnvironmentGate: env => !env.IsProduction()),
            new("AdminNav_SeedCampRoles", "DevSeed", "SeedCampRoles", null, null, "fa-solid fa-user-tag",  PolicyNames.AdminOnly,
                 EnvironmentGate: env => !env.IsProduction()),
        }),
    };
}

internal static class PillCounts
{
    public static async ValueTask<int?> ReviewQueue(IServiceProvider sp)
    {
        var onboarding = sp.GetRequiredService<Humans.Application.Interfaces.Onboarding.IOnboardingService>();
        var count = await onboarding.GetPendingReviewCountAsync();
        return count > 0 ? count : null;
    }

    public static async ValueTask<int?> VotingQueue(IServiceProvider sp)
    {
        var http = sp.GetRequiredService<IHttpContextAccessor>();
        var idClaim = http.HttpContext?.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);
        if (idClaim is null || !Guid.TryParse(idClaim.Value, out var userId))
            return null;
        var onboarding = sp.GetRequiredService<Humans.Application.Interfaces.Onboarding.IOnboardingService>();
        var count = await onboarding.GetUnvotedApplicationCountAsync(userId);
        return count > 0 ? count : null;
    }
}
```

- [ ] **Step 2: Build**

```bash
dotnet build Humans.slnx -v quiet
```

Expected: 0 errors. (May need `using Microsoft.Extensions.DependencyInjection;` for `GetRequiredService` — add if compiler complains.)

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/ViewComponents/AdminNavTree.cs
git commit -m "feat(admin-shell): configure admin sidebar tree (8 groups, 25 items)"
```

---

### Task 4: Skeleton `_AdminLayout.cshtml` (no sidebar yet)

**Files:**
- Create: `src/Humans.Web/Views/Shared/_AdminLayout.cshtml`

- [ ] **Step 1: Create the layout stub**

The `<head>` mirrors `_Layout.cshtml` lines 1-26 plus the two new stylesheets. The body is bare for now — sidebar and breadcrumb wire-up happens in Task 8.

```html
@* Admin shell layout — Renaissance aesthetic, ink-dark sidebar + parchment main.
   Inherited via _ViewStart.cshtml in admin-only view folders, or set per-view in
   mixed folders (e.g. Views/Profile/AdminList.cshtml). All admin-shell CSS rules
   are scoped under body.admin-shell to prevent leakage into member pages. *@
<!DOCTYPE html>
<html lang="@System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <meta name="description" content="Humans — Membership portal for Nobodies Collective" />
    <title>@ViewData["Title"] - @Localizer["Nav_Brand"]</title>
    <link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/bootstrap@5.3.3/dist/css/bootstrap.min.css"
          integrity="sha384-QWTKZyjpPEjISv5WaRU9OFeRpok6YctnYmDr5pNlyT2bRjXh0JMhjY6hW+ALEwIH"
          crossorigin="anonymous">
    <link rel="stylesheet" href="https://cdnjs.cloudflare.com/ajax/libs/font-awesome/6.5.1/css/all.min.css"
          integrity="sha512-DTOQO9RWCH3ppGqcWaEA1BIZOC6xxalwEsw9c2QQeAIftl+Vegovlnee1c9QX4TctnWMn13TZye+giMm8e2LwA=="
          crossorigin="anonymous"
          referrerpolicy="no-referrer">
    <link rel="stylesheet" href="https://cdn.jsdelivr.net/gh/lipis/flag-icons@7.2.3/css/flag-icons.min.css"
          crossorigin="anonymous">
    <link rel="preconnect" href="https://fonts.googleapis.com">
    <link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
    <link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Cormorant+Garamond:ital,wght@0,400;0,600;0,700;1,400;1,600&family=Source+Sans+3:wght@400;500;600;700&display=swap">
    <link rel="icon" href="~/img/favicon.svg" type="image/svg+xml">
    <link rel="stylesheet" href="~/css/site.css" />
    <link rel="stylesheet" href="~/css/tokens.css" />
    <link rel="stylesheet" href="~/css/admin-shell.css" />
    @await RenderSectionAsync("Styles", required: false)
</head>
<body class="admin-shell">
    <environment exclude="Production">
        <div class="env-banner">
            @{
                var envName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Non-Production";
                var label = envName.Equals("Staging", StringComparison.OrdinalIgnoreCase) ? "QA" : envName.ToUpperInvariant();
            }
            <span>@label &bull; @label &bull; @label &bull; @label &bull; @label &bull; @label &bull; @label &bull; @label &bull; @label &bull; @label &bull; @label</span>
        </div>
    </environment>

    <div class="app">
        <aside class="sidebar">
            @* AdminSidebar ViewComponent (Task 7) renders here *@
        </aside>
        <main class="main">
            <partial name="_AuthorizationPill" />
            @RenderBody()
        </main>
    </div>

    <div class="toast-container position-fixed bottom-0 end-0 p-3" style="z-index: 1080;" id="toastContainer"></div>

    <script src="https://cdn.jsdelivr.net/npm/bootstrap@5.3.3/dist/js/bootstrap.bundle.min.js"
            integrity="sha384-YvpcrYf0tY3lHB60NNkmXc5s9fDVZLESaAA55NDzOxhy9GkcIdslK1eN7N6jIeHz"
            crossorigin="anonymous"></script>
    <script src="~/js/site.js" asp-append-version="true"></script>
    @await RenderSectionAsync("Scripts", required: false)
    @await Component.InvokeAsync("FeedbackWidget")
</body>
</html>
```

- [ ] **Step 2: Build**

```bash
dotnet build Humans.slnx -v quiet
```

Expected: 0 errors. (No views set `Layout = "_AdminLayout"` yet, so the layout is unreferenced — that's fine.)

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/Views/Shared/_AdminLayout.cshtml
git commit -m "feat(admin-shell): add _AdminLayout skeleton (no sidebar yet)"
```

---

## Phase 2 — Sidebar ViewComponent

### Task 5: Write failing tests for `AdminSidebarViewComponent`

**Files:**
- Create: `src/Humans.Web.Tests/ViewComponents/AdminSidebarViewComponentTests.cs`

- [ ] **Step 1: Locate the test project**

```bash
find . -name "Humans.Web.Tests.csproj" -not -path "*/bin/*" -not -path "*/obj/*"
```

If no `Humans.Web.Tests` project exists, see whether `Humans.Tests` covers Web. Use whichever test project hosts ViewComponent tests today (grep for `: ViewComponent` in test files). Adjust the file path below accordingly.

- [ ] **Step 2: Write the test class**

```csharp
using System.Security.Claims;
using FluentAssertions;
using Humans.Domain.Constants;
using Humans.Web.ViewComponents;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Moq;
using Xunit;

namespace Humans.Web.Tests.ViewComponents;

public class AdminSidebarViewComponentTests
{
    [Fact]
    public async Task Hides_Items_When_Authorization_Fails()
    {
        var auth = new Mock<IAuthorizationService>();
        auth.Setup(a => a.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), It.IsAny<string>()))
            .ReturnsAsync(AuthorizationResult.Failed());
        var sut = MakeSut(auth.Object, "Home", "Index");
        var result = await sut.InvokeAsync() as ViewViewComponentResult;
        var model = result!.ViewData!.Model as AdminSidebarViewModel;
        model!.Groups.Should().BeEmpty();
    }

    [Fact]
    public async Task Hides_Empty_Groups()
    {
        var auth = new Mock<IAuthorizationService>();
        // Allow only items in the Operations group
        auth.Setup(a => a.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(),
                It.Is<string>(p => p == PolicyNames.VolunteerSectionAccess || p == PolicyNames.TicketAdminBoardOrAdmin)))
            .ReturnsAsync(AuthorizationResult.Success());
        auth.Setup(a => a.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(),
                It.Is<string>(p => p != PolicyNames.VolunteerSectionAccess && p != PolicyNames.TicketAdminBoardOrAdmin)))
            .ReturnsAsync(AuthorizationResult.Failed());
        var sut = MakeSut(auth.Object, "Vol", "Index");
        var result = await sut.InvokeAsync() as ViewViewComponentResult;
        var model = result!.ViewData!.Model as AdminSidebarViewModel;
        model!.Groups.Should().HaveCount(1);
        model.Groups.Single().LabelKey.Should().Be("AdminGroup_Operations");
    }

    [Fact]
    public async Task Marks_Active_Item_From_RouteData()
    {
        var auth = AlwaysAllow();
        var sut = MakeSut(auth, "Ticket", "Index");
        var result = await sut.InvokeAsync() as ViewViewComponentResult;
        var model = result!.ViewData!.Model as AdminSidebarViewModel;
        var ticketsItem = model!.Groups.SelectMany(g => g.Items)
            .Single(i => i.LabelKey == "AdminNav_Tickets");
        ticketsItem.IsActive.Should().BeTrue();
        var volunteersItem = model.Groups.SelectMany(g => g.Items)
            .Single(i => i.LabelKey == "AdminNav_Volunteers");
        volunteersItem.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task Hides_Dev_Group_In_Production()
    {
        var env = new Mock<IWebHostEnvironment>();
        env.SetupGet(e => e.EnvironmentName).Returns("Production");
        var sut = MakeSut(AlwaysAllow(), "Home", "Index", env.Object);
        var result = await sut.InvokeAsync() as ViewViewComponentResult;
        var model = result!.ViewData!.Model as AdminSidebarViewModel;
        model!.Groups.Should().NotContain(g => g.LabelKey == "AdminGroup_Dev");
    }

    private static IAuthorizationService AlwaysAllow()
    {
        var auth = new Mock<IAuthorizationService>();
        auth.Setup(a => a.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), It.IsAny<string>()))
            .ReturnsAsync(AuthorizationResult.Success());
        return auth.Object;
    }

    private static AdminSidebarViewComponent MakeSut(
        IAuthorizationService auth, string controller, string action, IWebHostEnvironment? env = null)
    {
        env ??= MakeDevEnv();
        var sp = new Mock<IServiceProvider>();
        var http = new Mock<IHttpContextAccessor>();
        var sut = new AdminSidebarViewComponent(auth, env, sp.Object, http.Object);

        var viewContext = new Microsoft.AspNetCore.Mvc.Rendering.ViewContext
        {
            RouteData = new RouteData
            {
                Values = { ["controller"] = controller, ["action"] = action }
            },
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) }
        };
        var componentContext = new ViewComponentContext { ViewContext = viewContext };
        sut.ViewComponentContext = componentContext;
        return sut;
    }

    private static IWebHostEnvironment MakeDevEnv()
    {
        var env = new Mock<IWebHostEnvironment>();
        env.SetupGet(e => e.EnvironmentName).Returns("Development");
        return env.Object;
    }
}
```

- [ ] **Step 3: Run tests, expect compilation failure**

```bash
dotnet test Humans.slnx --filter FullyQualifiedName~AdminSidebarViewComponentTests -v quiet
```

Expected: build fails — `AdminSidebarViewComponent` and `AdminSidebarViewModel` don't exist yet.

- [ ] **Step 4: Commit the failing test**

```bash
git add src/Humans.Web.Tests/ViewComponents/AdminSidebarViewComponentTests.cs
git commit -m "test(admin-shell): add failing AdminSidebarViewComponent tests"
```

---

### Task 6: Implement `AdminSidebarViewComponent` to make tests pass

**Files:**
- Create: `src/Humans.Web/ViewComponents/AdminSidebarViewComponent.cs`
- Create: `src/Humans.Web/ViewComponents/AdminSidebarViewModel.cs`

- [ ] **Step 1: Create the view model**

```csharp
namespace Humans.Web.ViewComponents;

public sealed record AdminSidebarViewModel(IReadOnlyList<AdminSidebarGroupViewModel> Groups);

public sealed record AdminSidebarGroupViewModel(string LabelKey, IReadOnlyList<AdminSidebarItemViewModel> Items);

public sealed record AdminSidebarItemViewModel(
    string LabelKey,
    string? Controller,
    string? Action,
    object? RouteValues,
    string? RawHref,
    string IconCssClass,
    bool IsActive,
    int? PillCount);
```

- [ ] **Step 2: Create the ViewComponent**

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.ViewComponents;

public sealed class AdminSidebarViewComponent : ViewComponent
{
    private readonly IAuthorizationService _authorization;
    private readonly IWebHostEnvironment _environment;
    private readonly IServiceProvider _serviceProvider;
    private readonly IHttpContextAccessor _httpContext;

    public AdminSidebarViewComponent(
        IAuthorizationService authorization,
        IWebHostEnvironment environment,
        IServiceProvider serviceProvider,
        IHttpContextAccessor httpContext)
    {
        _authorization = authorization;
        _environment = environment;
        _serviceProvider = serviceProvider;
        _httpContext = httpContext;
    }

    public async Task<IViewComponentResult> InvokeAsync()
    {
        var activeController = (string?)RouteData.Values["controller"];
        var visibleGroups = new List<AdminSidebarGroupViewModel>(AdminNavTree.Groups.Count);

        foreach (var group in AdminNavTree.Groups)
        {
            var visibleItems = new List<AdminSidebarItemViewModel>(group.Items.Count);
            foreach (var item in group.Items)
            {
                if (item.EnvironmentGate is not null && !item.EnvironmentGate(_environment))
                    continue;

                if (item.Policy is not null)
                {
                    var auth = await _authorization.AuthorizeAsync(HttpContext.User, null, item.Policy);
                    if (!auth.Succeeded) continue;
                }
                else if (item.RoleCheck is not null && !item.RoleCheck(HttpContext.User))
                {
                    continue;
                }

                int? pill = null;
                if (item.PillCount is not null)
                    pill = await item.PillCount(_serviceProvider);

                visibleItems.Add(new AdminSidebarItemViewModel(
                    LabelKey: item.LabelKey,
                    Controller: item.Controller,
                    Action: item.Action,
                    RouteValues: item.RouteValues,
                    RawHref: item.RawHref,
                    IconCssClass: item.IconCssClass,
                    IsActive: !string.IsNullOrEmpty(item.Controller)
                              && string.Equals(item.Controller, activeController, StringComparison.OrdinalIgnoreCase),
                    PillCount: pill));
            }

            if (visibleItems.Count > 0)
                visibleGroups.Add(new AdminSidebarGroupViewModel(group.LabelKey, visibleItems));
        }

        return View(new AdminSidebarViewModel(visibleGroups));
    }
}
```

- [ ] **Step 3: Create the empty `Default.cshtml` so MVC finds a view**

```html
@model Humans.Web.ViewComponents.AdminSidebarViewModel
@* Render filled in Task 9 — for now Default.cshtml exists so MVC's view-discovery succeeds. *@
@foreach (var group in Model.Groups)
{
    <div data-group="@group.LabelKey"></div>
}
```

Path: `src/Humans.Web/Views/Shared/Components/AdminSidebar/Default.cshtml`

- [ ] **Step 4: Run tests, expect pass**

```bash
dotnet test Humans.slnx --filter FullyQualifiedName~AdminSidebarViewComponentTests -v quiet
```

Expected: 4 passed, 0 failed.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Web/ViewComponents/AdminSidebarViewComponent.cs \
        src/Humans.Web/ViewComponents/AdminSidebarViewModel.cs \
        src/Humans.Web/Views/Shared/Components/AdminSidebar/Default.cshtml
git commit -m "feat(admin-shell): implement AdminSidebarViewComponent"
```

---

## Phase 3 — Breadcrumb ViewComponent

### Task 7: `AdminBreadcrumbViewComponent`

**Files:**
- Create: `src/Humans.Web.Tests/ViewComponents/AdminBreadcrumbViewComponentTests.cs`
- Create: `src/Humans.Web/ViewComponents/AdminBreadcrumbViewComponent.cs`
- Create: `src/Humans.Web/Views/Shared/Components/AdminBreadcrumb/Default.cshtml`

- [ ] **Step 1: Failing test**

```csharp
using FluentAssertions;
using Humans.Web.ViewComponents;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace Humans.Web.Tests.ViewComponents;

public class AdminBreadcrumbViewComponentTests
{
    [Fact]
    public void Resolves_Group_And_Item_For_Known_Controller()
    {
        var sut = new AdminBreadcrumbViewComponent();
        var ctx = new ViewComponentContext
        {
            ViewContext = new Microsoft.AspNetCore.Mvc.Rendering.ViewContext
            {
                RouteData = new RouteData { Values = { ["controller"] = "Ticket", ["action"] = "Index" } }
            }
        };
        sut.ViewComponentContext = ctx;
        var result = sut.Invoke() as ViewViewComponentResult;
        var model = result!.ViewData!.Model as AdminBreadcrumbViewModel;
        model!.GroupLabelKey.Should().Be("AdminGroup_Operations");
        model.ItemLabelKey.Should().Be("AdminNav_Tickets");
    }

    [Fact]
    public void Falls_Back_To_PageTitle_For_Unknown_Controller()
    {
        var sut = new AdminBreadcrumbViewComponent();
        var ctx = new ViewComponentContext
        {
            ViewContext = new Microsoft.AspNetCore.Mvc.Rendering.ViewContext
            {
                RouteData = new RouteData { Values = { ["controller"] = "Unknown", ["action"] = "Index" } },
                ViewData = new ViewDataDictionary(new Microsoft.AspNetCore.Mvc.ModelBinding.EmptyModelMetadataProvider(), new Microsoft.AspNetCore.Mvc.ModelBinding.ModelStateDictionary())
                {
                    ["Title"] = "Some Page"
                }
            }
        };
        sut.ViewComponentContext = ctx;
        var result = sut.Invoke() as ViewViewComponentResult;
        var model = result!.ViewData!.Model as AdminBreadcrumbViewModel;
        model!.GroupLabelKey.Should().BeNull();
        model.ItemLabelKey.Should().BeNull();
        model.FallbackTitle.Should().Be("Some Page");
    }
}
```

- [ ] **Step 2: Run test, expect compile failure**

```bash
dotnet test Humans.slnx --filter FullyQualifiedName~AdminBreadcrumbViewComponentTests -v quiet
```

Expected: build fails — `AdminBreadcrumbViewComponent` undefined.

- [ ] **Step 3: Implement view component + view model**

```csharp
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.ViewComponents;

public sealed record AdminBreadcrumbViewModel(string? GroupLabelKey, string? ItemLabelKey, string? FallbackTitle);

public sealed class AdminBreadcrumbViewComponent : ViewComponent
{
    public IViewComponentResult Invoke()
    {
        var controller = (string?)RouteData.Values["controller"];
        foreach (var group in AdminNavTree.Groups)
        {
            foreach (var item in group.Items)
            {
                if (string.Equals(item.Controller, controller, StringComparison.OrdinalIgnoreCase))
                    return View(new AdminBreadcrumbViewModel(group.LabelKey, item.LabelKey, null));
            }
        }
        var title = ViewData["Title"] as string;
        return View(new AdminBreadcrumbViewModel(null, null, title));
    }
}
```

- [ ] **Step 4: Default.cshtml**

Path: `src/Humans.Web/Views/Shared/Components/AdminBreadcrumb/Default.cshtml`

```html
@model Humans.Web.ViewComponents.AdminBreadcrumbViewModel
<a asp-controller="Admin" asp-action="Index">@Localizer["AdminNav_Brand"]</a>
@if (Model.GroupLabelKey is not null)
{
    <span class="sep">/</span><span>@Localizer[Model.GroupLabelKey]</span>
    <span class="sep">/</span><span class="here">@Localizer[Model.ItemLabelKey!]</span>
}
else if (!string.IsNullOrEmpty(Model.FallbackTitle))
{
    <span class="sep">/</span><span class="here">@Model.FallbackTitle</span>
}
```

- [ ] **Step 5: Run tests, expect pass**

```bash
dotnet test Humans.slnx --filter FullyQualifiedName~AdminBreadcrumbViewComponentTests -v quiet
```

Expected: 2 passed.

- [ ] **Step 6: Commit**

```bash
git add src/Humans.Web/ViewComponents/AdminBreadcrumbViewComponent.cs \
        src/Humans.Web/Views/Shared/Components/AdminBreadcrumb/Default.cshtml \
        src/Humans.Web.Tests/ViewComponents/AdminBreadcrumbViewComponentTests.cs
git commit -m "feat(admin-shell): add AdminBreadcrumbViewComponent"
```

---

## Phase 4 — Wire layout for `/Admin` only (smoke test the shell)

### Task 8: Drop full `admin-shell.css`

**Files:**
- Modify: `src/Humans.Web/wwwroot/css/admin-shell.css`

- [ ] **Step 1: Replace empty `admin-shell.css` with the full styles**

The reference for these styles is `humans-design-system/project/ui-kits/admin-console.html` and `humans-design-system/project/components/navigation.html`. Copy the inline `<style>` blocks from those two files, prefix every selector with `body.admin-shell`, and add the mobile offcanvas + responsive breakpoints below.

Required class set (must be present, all scoped under `body.admin-shell`):

- `.app` — CSS grid container, `grid-template-columns: 240px 1fr` at ≥768px, single-column below.
- `.sidebar` — ink-dark background, sticky on desktop.
- `.sidebar .brand`, `.sidebar h4`, `.sidebar a`, `.sidebar a.active`, `.sidebar .pill`, `.sidebar .me`, `.sidebar .me .avatar`.
- `.main` — parchment background, padded.
- `.crumb`, `.crumb a`, `.crumb .sep`, `.crumb .here`.
- `.page-head`, `h1.title`, `h1.title em`, `.sub`.
- `.stats`, `.stat`, `.stat .label`, `.stat .value`, `.stat .value em`, `.stat .delta`, `.stat .delta.bad`.
- `.card`, `.card-head`, `.card-head h3`, `.card-head small`.
- `.staffing`, `.dept-row`, `.dept-row .name`, `.dept-row .track`, `.dept-row .track > div`, `.dept-row .track.low > div`, `.dept-row .track.crit > div`, `.dept-row .num`.
- `.activity-item`, `.activity-item .bubble`, `.activity-item strong`, `.activity-item p`, `.activity-item .time`.
- `.split-panels` — `1fr 340px` at ≥1024px, single column below.

Mobile additions (not in the source design):

```css
body.admin-shell .admin-mobile-header {
    display: none;
    background: var(--h-aged-ink);
    color: var(--h-parchment);
    height: 56px;
    padding: 0 16px;
    align-items: center;
    gap: 12px;
}
body.admin-shell .admin-mobile-header .hamburger {
    background: transparent; border: 0; color: var(--h-parchment); font-size: 1.2rem;
}
body.admin-shell .admin-mobile-header .crumb { color: var(--h-parchment); flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }

@media (max-width: 767.98px) {
    body.admin-shell .app { grid-template-columns: 1fr; }
    body.admin-shell > .app > aside.sidebar { display: none; }
    body.admin-shell .admin-mobile-header { display: flex; }
    body.admin-shell .stats { grid-template-columns: 1fr; }
    body.admin-shell .split-panels { grid-template-columns: 1fr; }
    body.admin-shell .offcanvas.admin-sidebar-offcanvas { background: var(--h-aged-ink); width: 280px; }
}
@media (min-width: 768px) and (max-width: 1023.98px) {
    body.admin-shell .stats { grid-template-columns: repeat(2, 1fr); }
    body.admin-shell .split-panels { grid-template-columns: 1fr; }
}
```

- [ ] **Step 2: Build and serve, smoke test desktop + mobile viewport**

```bash
dotnet run --project src/Humans.Web
```

Browse `https://nuc.home:5001/Admin` (or whatever the dev port is). The page still renders with the OLD `_Layout` because `_ViewStart.cshtml` for `Views/Admin/` doesn't yet point to `_AdminLayout` — that's Task 9. CSS is loaded but unused on this page; just confirm no 404s for `tokens.css` or `admin-shell.css` in DevTools network panel.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/wwwroot/css/admin-shell.css
git commit -m "feat(admin-shell): full Renaissance admin-shell styles + mobile offcanvas"
```

---

### Task 9: Wire `_AdminLayout` with sidebar, breadcrumb, mobile offcanvas

**Files:**
- Modify: `src/Humans.Web/Views/Shared/_AdminLayout.cshtml`
- Modify: `src/Humans.Web/Views/Shared/Components/AdminSidebar/Default.cshtml`

- [ ] **Step 1: Replace the placeholder `Default.cshtml` of `AdminSidebar`**

```html
@model Humans.Web.ViewComponents.AdminSidebarViewModel

<div class="sidebar-scroll">
    <a class="brand" asp-controller="Home" asp-action="Index">
        Humans<small>@Localizer["AdminNav_BrandSubtitle"]</small>
    </a>

    @foreach (var group in Model.Groups)
    {
        <h4>@Localizer[group.LabelKey]</h4>
        @foreach (var item in group.Items)
        {
            <a class="@(item.IsActive ? "active" : null)"
               @if (item.RawHref is not null)
               {
                   <text>href="@item.RawHref"</text>
               }
               else
               {
                   <text>asp-controller="@item.Controller" asp-action="@item.Action"</text>
               }>
                <i class="@item.IconCssClass" aria-hidden="true"></i>
                <span>@Localizer[item.LabelKey]</span>
                @if (item.PillCount.HasValue)
                {
                    <span class="pill">@item.PillCount.Value</span>
                }
            </a>
        }
    }

    <div style="flex:1;"></div>
    <partial name="_AdminSidebarFooter" />
</div>
```

(Note: the inline-`@if` for the `href`-vs-`asp-controller` choice may need to be split into two `<a>` tags depending on Razor parser behavior — if the build fails, write two branches with the full `<a>...</a>` in each.)

- [ ] **Step 2: Create the sidebar footer partial**

Path: `src/Humans.Web/Views/Shared/_AdminSidebarFooter.cshtml`

```html
@using Microsoft.AspNetCore.Identity
@using Humans.Domain.Entities
@inject SignInManager<User> SignInManager
@inject UserManager<User> UserManager

@{
    var user = User.Identity?.IsAuthenticated == true ? await UserManager.GetUserAsync(User) : null;
    var initials = user is null ? "?" : string.Concat(user.UserName?.Split(' ').Select(s => s.Length > 0 ? s[0] : '?').Take(2) ?? new[] { '?' });
    var roleLabel = AdminUserRoleSummary.PrimaryRole(User);
}

<div class="me dropdown dropup">
    <a class="d-flex align-items-center text-decoration-none w-100" href="#" data-bs-toggle="dropdown" aria-expanded="false">
        <div class="avatar">@initials</div>
        <div class="ms-2 text-truncate">
            <strong>@(user?.UserName ?? "—")</strong>
            <small>@roleLabel</small>
        </div>
    </a>
    <ul class="dropdown-menu dropdown-menu-dark">
        <li><a class="dropdown-item" asp-controller="Profile" asp-action="Index">@Localizer["Nav_Profile"]</a></li>
        <li><a class="dropdown-item" asp-controller="Home" asp-action="About">@Localizer["About"]</a></li>
        <li><a class="dropdown-item" asp-controller="Home" asp-action="Privacy">@Localizer["Privacy"]</a></li>
        <li><hr class="dropdown-divider"></li>
        <li>
            <form asp-controller="Account" asp-action="Logout" method="post">
                <button type="submit" class="dropdown-item">@Localizer["Logout"]</button>
            </form>
        </li>
    </ul>
</div>
```

- [ ] **Step 3: Create `AdminUserRoleSummary` helper**

Path: `src/Humans.Web/Authorization/AdminUserRoleSummary.cs`

```csharp
using System.Security.Claims;
using Humans.Domain.Constants;

namespace Humans.Web.Authorization;

/// <summary>
/// Picks a single label representing the user's most-privileged admin role,
/// for display in the admin sidebar's footer. Order is roughly broadest-scope
/// to narrowest.
/// </summary>
public static class AdminUserRoleSummary
{
    private static readonly string[] Order =
    {
        RoleNames.Admin, RoleNames.Board, RoleNames.HumanAdmin, RoleNames.FinanceAdmin,
        RoleNames.TicketAdmin, RoleNames.TeamsAdmin, RoleNames.CampAdmin,
        RoleNames.FeedbackAdmin, RoleNames.NoInfoAdmin, RoleNames.VolunteerCoordinator,
        RoleNames.ConsentCoordinator,
    };

    public static string PrimaryRole(ClaimsPrincipal user)
    {
        foreach (var role in Order)
            if (user.IsInRole(role)) return role;
        return string.Empty;
    }
}
```

- [ ] **Step 4: Update `_AdminLayout.cshtml` body to include sidebar + breadcrumb + mobile offcanvas**

Replace the body block from Task 4 with:

```html
<body class="admin-shell">
    <environment exclude="Production">
        <div class="env-banner">
            @{
                var envName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Non-Production";
                var label = envName.Equals("Staging", StringComparison.OrdinalIgnoreCase) ? "QA" : envName.ToUpperInvariant();
            }
            <span>@label &bull; @label &bull; @label &bull; @label &bull; @label &bull; @label &bull; @label &bull; @label &bull; @label &bull; @label &bull; @label</span>
        </div>
    </environment>

    <div class="app">
        <aside class="sidebar d-none d-md-block">
            @await Component.InvokeAsync("AdminSidebar")
        </aside>

        <main class="main">
            <div class="admin-mobile-header d-md-none">
                <button class="hamburger" type="button" data-bs-toggle="offcanvas"
                        data-bs-target="#adminSidebarOffcanvas" aria-controls="adminSidebarOffcanvas"
                        aria-label="@Localizer["AdminNav_OpenSidebar"]">
                    <i class="fa-solid fa-bars"></i>
                </button>
                <div class="crumb">
                    @await Component.InvokeAsync("AdminBreadcrumb")
                </div>
            </div>

            <div class="crumb d-none d-md-flex">
                @await Component.InvokeAsync("AdminBreadcrumb")
            </div>

            <partial name="_AuthorizationPill" />
            @RenderBody()
        </main>
    </div>

    <div class="offcanvas offcanvas-start admin-sidebar-offcanvas d-md-none"
         tabindex="-1" id="adminSidebarOffcanvas" aria-labelledby="adminSidebarOffcanvasLabel">
        <div class="offcanvas-header">
            <h5 class="offcanvas-title text-light" id="adminSidebarOffcanvasLabel">@Localizer["AdminNav_BrandSubtitle"]</h5>
            <button type="button" class="btn-close btn-close-white" data-bs-dismiss="offcanvas" aria-label="@Localizer["Close"]"></button>
        </div>
        <div class="offcanvas-body p-0">
            <aside class="sidebar w-100">
                @await Component.InvokeAsync("AdminSidebar")
            </aside>
        </div>
    </div>

    <div class="toast-container position-fixed bottom-0 end-0 p-3" style="z-index: 1080;" id="toastContainer"></div>

    <script src="https://cdn.jsdelivr.net/npm/bootstrap@5.3.3/dist/js/bootstrap.bundle.min.js"
            integrity="sha384-YvpcrYf0tY3lHB60NNkmXc5s9fDVZLESaAA55NDzOxhy9GkcIdslK1eN7N6jIeHz"
            crossorigin="anonymous"></script>
    <script src="~/js/site.js" asp-append-version="true"></script>
    @await RenderSectionAsync("Scripts", required: false)
    @await Component.InvokeAsync("FeedbackWidget")
</body>
```

- [ ] **Step 5: Add `_ViewStart.cshtml` in `Views/Admin/` to opt that folder into the new layout**

Path: `src/Humans.Web/Views/Admin/_ViewStart.cshtml`

```cshtml
@{
    Layout = "_AdminLayout";
}
```

- [ ] **Step 6: Build, run, smoke test**

```bash
dotnet build Humans.slnx -v quiet
dotnet run --project src/Humans.Web
```

Sign in as an Admin user; visit `/Admin`. Expected:
- Ink-dark sidebar on the left (≥768px).
- Sidebar lists every group your role permits (Admin role sees all groups except `Dev` if non-prod is off).
- Breadcrumb shows `Admin / Dashboard` (or whatever the current Admin/Index `ViewData["Title"]` is — fallback path for now since `Admin/Index` isn't an `AdminNavTree` entry).
- Resize to <768px: sidebar collapses; hamburger appears in a thin top bar; tap to open offcanvas.

- [ ] **Step 7: Commit**

```bash
git add src/Humans.Web/Views/Shared/_AdminLayout.cshtml \
        src/Humans.Web/Views/Shared/Components/AdminSidebar/Default.cshtml \
        src/Humans.Web/Views/Shared/_AdminSidebarFooter.cshtml \
        src/Humans.Web/Authorization/AdminUserRoleSummary.cs \
        src/Humans.Web/Views/Admin/_ViewStart.cshtml
git commit -m "feat(admin-shell): wire sidebar + breadcrumb + mobile offcanvas; opt /Admin into shell"
```

---

## Phase 5 — Dashboard service surface

### Task 10: Add `IProfileService.GetActiveApprovedCountAsync`

**Files:**
- Modify: `src/Humans.Application/Interfaces/Profiles/IProfileService.cs`
- Modify: `src/Humans.Infrastructure/Services/ProfileService.cs` (or wherever `IProfileService` is implemented — grep `class.*IProfileService`)
- Modify: `src/Humans.Tests/Profiles/ProfileServiceTests.cs` (or wherever Profile service tests live)

- [ ] **Step 1: Add the failing test**

```csharp
[Fact]
public async Task GetActiveApprovedCountAsync_Returns_Count_Of_Approved_NonSuspended_Profiles()
{
    // Arrange: seed 3 approved-active, 1 suspended, 1 unapproved
    // (use existing test seeding helpers — see other ProfileServiceTests for the pattern)
    // Act
    var count = await Sut.GetActiveApprovedCountAsync(CancellationToken.None);
    // Assert
    count.Should().Be(3);
}
```

- [ ] **Step 2: Run test, expect compile failure**

```bash
dotnet test Humans.slnx --filter "FullyQualifiedName~GetActiveApprovedCountAsync" -v quiet
```

Expected: build fails — method not on interface.

- [ ] **Step 3: Add interface method**

In `IProfileService.cs`, near `GetActiveApprovedUserIdsAsync`:

```csharp
/// <summary>
/// Returns the count of profiles whose status is approved and not suspended.
/// Used by the admin dashboard "Active humans" stat tile. At ~500-user scale
/// this can be a simple Count query — no caching required.
/// </summary>
Task<int> GetActiveApprovedCountAsync(CancellationToken ct = default);
```

- [ ] **Step 4: Implement in service**

The existing `GetActiveApprovedUserIdsAsync` shows the filter shape — copy it but project to `Count()`:

```csharp
public async Task<int> GetActiveApprovedCountAsync(CancellationToken ct = default) =>
    await _profileRepository.CountActiveApprovedAsync(ct);
```

If the repository doesn't have `CountActiveApprovedAsync`, add it the same way `GetActiveApprovedUserIds` is implemented and project with `.CountAsync(ct)` instead of `.Select(...).ToListAsync()`.

- [ ] **Step 5: Run test, expect pass**

```bash
dotnet test Humans.slnx --filter "FullyQualifiedName~GetActiveApprovedCountAsync" -v quiet
```

Expected: pass.

- [ ] **Step 6: Commit**

```bash
git add src/Humans.Application/Interfaces/Profiles/IProfileService.cs \
        src/Humans.Infrastructure/Services/ProfileService.cs \
        src/Humans.Application/Interfaces/Repositories/IProfileRepository.cs \
        src/Humans.Infrastructure/Repositories/ProfileRepository.cs \
        src/Humans.Tests/Profiles/ProfileServiceTests.cs
git commit -m "feat(profiles): add GetActiveApprovedCountAsync for admin dashboard"
```

(Adjust file paths to match the project's actual service/repo locations.)

---

### Task 11: Confirm `IFeedbackService.GetActionableCountAsync` exists; no work needed

`IFeedbackService.GetActionableCountAsync(CancellationToken)` already exists (`src/Humans.Application/Interfaces/Feedback/IFeedbackService.cs:42-43`). The dashboard tile uses it directly. No changes — skip to Task 12.

---

### Task 12: Confirm `IAuditLogService.GetRecentAsync` exists

`IAuditLogService.GetRecentAsync(int count, CancellationToken)` already exists (`src/Humans.Application/Interfaces/AuditLog/IAuditLogService.cs:54`). The activity feed uses it directly. No changes — skip to Task 13.

---

### Task 13: Add shift coverage method (only if absent)

**Files:**
- Possibly modify: `src/Humans.Application/Interfaces/Shifts/IShiftManagementService.cs`

- [ ] **Step 1: Grep the existing service for an overall-coverage method**

```bash
grep -n -i "coverage\|fillrate\|filledcount\|TotalShifts" \
    src/Humans.Application/Interfaces/Shifts/IShiftManagementService.cs \
    src/Humans.Infrastructure/Services/ShiftManagementService.cs
```

If a method already returns `(filledCount, totalCount)` or `coveragePct`, use it in the dashboard. Stop here.

- [ ] **Step 2: If absent, add a thin method**

```csharp
/// <summary>
/// Returns overall shift coverage for the active event:
/// (filled signups / total slots, plus the ratio).
/// Returns (0, 0, 0d) if no event is active.
/// Used by the admin dashboard's shift-coverage stat tile.
/// </summary>
Task<(int Filled, int Total, double Ratio)> GetOverallCoverageAsync(CancellationToken ct = default);
```

- [ ] **Step 3: Implement using the same data the existing dashboard service uses**

`IDashboardService.GetMemberDashboardAsync` already aggregates shift state per-user; the underlying repository method that totals shifts/signups is reused here. Look at how `IDashboardService`'s implementation pulls shift data (start with `IShiftSignupRepository` and `IShiftManagementRepository`) and aggregate without per-user filtering.

- [ ] **Step 4: Test + run + commit**

If the method was added, write a test that seeds 10 slots and 7 signups and asserts `(7, 10, 0.7)`. If skipped, no commit needed for this task.

---

### Task 14: Add `IAdminDashboardService` for system health

**Files:**
- Create: `src/Humans.Application/Interfaces/Admin/IAdminDashboardService.cs`
- Create: `src/Humans.Infrastructure/Services/AdminDashboardService.cs`
- Create: `src/Humans.Tests/Admin/AdminDashboardServiceTests.cs`
- Modify: DI registration (search `AddSingleton<IAuditLogService` to find the section's registration file and add `IAdminDashboardService` near it)

- [ ] **Step 1: Failing test**

```csharp
using FluentAssertions;
using Humans.Application.Interfaces.Admin;
using Xunit;

public class AdminDashboardServiceTests
{
    [Fact]
    public async Task GetSystemHealthAsync_Returns_Counts_Of_Errors_And_FailedJobs()
    {
        // Arrange: stub log store with 3 ERROR entries in last 24h, Hangfire monitor with 2 failed jobs
        // Act
        var result = await Sut.GetSystemHealthAsync(CancellationToken.None);
        // Assert
        result.ErrorsLast24h.Should().Be(3);
        result.FailedJobs.Should().Be(2);
        result.AllNormal.Should().BeFalse();
    }

    [Fact]
    public async Task GetSystemHealthAsync_AllNormal_When_Zero_Errors_And_Zero_Failed_Jobs()
    {
        var result = await Sut.GetSystemHealthAsync(CancellationToken.None);
        result.AllNormal.Should().BeTrue();
    }
}
```

- [ ] **Step 2: Run test, expect compile failure**

```bash
dotnet test Humans.slnx --filter "FullyQualifiedName~AdminDashboardServiceTests" -v quiet
```

- [ ] **Step 3: Define the interface**

```csharp
namespace Humans.Application.Interfaces.Admin;

public sealed record AdminSystemHealth(int ErrorsLast24h, int FailedJobs)
{
    public bool AllNormal => ErrorsLast24h == 0 && FailedJobs == 0;
}

public interface IAdminDashboardService
{
    Task<AdminSystemHealth> GetSystemHealthAsync(CancellationToken ct = default);
}
```

- [ ] **Step 4: Implement**

The implementation reads from whichever log store is in use — likely `Microsoft.Extensions.Logging` does NOT expose query — so this method needs the same backing source the existing `Admin/Logs` view reads. Grep `Views/Admin/Logs.cshtml` and the controller action to find the source (e.g., a `ILogQuery` service or direct SQL into a logs table). For Hangfire, use `Hangfire.JobStorage.Current.GetMonitoringApi().FailedCount()`.

```csharp
using Hangfire;
using Humans.Application.Interfaces.Admin;

public sealed class AdminDashboardService : IAdminDashboardService
{
    private readonly ILogQueryService _logs;  // adjust to actual existing log-query service
    public AdminDashboardService(ILogQueryService logs) { _logs = logs; }

    public async Task<AdminSystemHealth> GetSystemHealthAsync(CancellationToken ct = default)
    {
        var since = DateTimeOffset.UtcNow.AddHours(-24);
        var errors = await _logs.CountAtLeastErrorAsync(since, ct);
        var failed = (int)JobStorage.Current.GetMonitoringApi().FailedCount();
        return new AdminSystemHealth(errors, failed);
    }
}
```

If the log store doesn't expose a count method, add one as a small extension to whichever interface the Admin/Logs view uses today. If that's not feasible, return `0` for `ErrorsLast24h` and a `// TODO` comment — the tile will read "0 errors" until the count method lands.

- [ ] **Step 5: Register in DI**

Search for `IAuditLogService` registration:

```bash
grep -rn "AddSingleton<IAuditLogService\|AddScoped<IAuditLogService\|AddTransient<IAuditLogService" src/
```

Add this line in the same registration block:

```csharp
services.AddScoped<IAdminDashboardService, AdminDashboardService>();
```

- [ ] **Step 6: Run test, expect pass**

```bash
dotnet test Humans.slnx --filter "FullyQualifiedName~AdminDashboardServiceTests" -v quiet
```

- [ ] **Step 7: Commit**

```bash
git add src/Humans.Application/Interfaces/Admin/IAdminDashboardService.cs \
        src/Humans.Infrastructure/Services/AdminDashboardService.cs \
        src/Humans.Tests/Admin/AdminDashboardServiceTests.cs \
        src/Humans.Web/Program.cs   # or wherever DI registration lives
git commit -m "feat(admin-shell): add IAdminDashboardService for system-health composite"
```

---

## Phase 6 — Dashboard view

### Task 15: `AdminDashboardViewModel`

**Files:**
- Create: `src/Humans.Web/Models/AdminDashboardViewModel.cs`

- [ ] **Step 1: Create the model**

```csharp
using Humans.Domain.Entities;

namespace Humans.Web.Models;

public sealed record AdminDashboardViewModel(
    string GreetingFirstName,
    int ActiveHumans,
    int ShiftCoveragePercent,
    int? ShiftFilledOf,
    int? ShiftTotalOf,
    int OpenFeedback,
    int ErrorsLast24h,
    int FailedJobs,
    bool SystemAllNormal,
    IReadOnlyList<DepartmentCoverage> StaffingByDepartment,
    IReadOnlyList<AuditLogEntry> RecentActivity);

public sealed record DepartmentCoverage(string Name, int Filled, int Total)
{
    public double Ratio => Total > 0 ? (double)Filled / Total : 0;
    public string TrackClass => Ratio >= 0.7 ? "" : Ratio >= 0.5 ? "low" : "crit";
}
```

- [ ] **Step 2: Build, commit**

```bash
dotnet build Humans.slnx -v quiet
git add src/Humans.Web/Models/AdminDashboardViewModel.cs
git commit -m "feat(admin-shell): AdminDashboardViewModel"
```

---

### Task 16: `AdminController.Index` returns the model

**Files:**
- Modify: `src/Humans.Web/Controllers/AdminController.cs`

- [ ] **Step 1: Update `Index()` and inject the new services**

```csharp
[HttpGet("")]
[Authorize(Policy = PolicyNames.AdminOnly)]
public async Task<IActionResult> Index(
    IProfileService profileService,
    IShiftManagementService shifts,
    IFeedbackService feedback,
    IAuditLogService auditLog,
    IAdminDashboardService adminDashboard,
    CancellationToken ct)
{
    var firstName = User.Identity?.Name?.Split(' ').FirstOrDefault() ?? "";
    var activeHumans = await profileService.GetActiveApprovedCountAsync(ct);
    var (filled, total, ratio) = await shifts.GetOverallCoverageAsync(ct);   // see Task 13 fallback
    var openFeedback = await feedback.GetActionableCountAsync(ct);
    var health = await adminDashboard.GetSystemHealthAsync(ct);
    var recent = await auditLog.GetRecentAsync(8, ct);
    var staffing = Array.Empty<DepartmentCoverage>();   // Phase 4-follow-up: real per-department coverage

    var vm = new AdminDashboardViewModel(
        GreetingFirstName: firstName,
        ActiveHumans: activeHumans,
        ShiftCoveragePercent: total > 0 ? (int)Math.Round(ratio * 100) : 0,
        ShiftFilledOf: total > 0 ? filled : null,
        ShiftTotalOf: total > 0 ? total : null,
        OpenFeedback: openFeedback,
        ErrorsLast24h: health.ErrorsLast24h,
        FailedJobs: health.FailedJobs,
        SystemAllNormal: health.AllNormal,
        StaffingByDepartment: staffing,
        RecentActivity: recent);
    return View(vm);
}
```

(If the constructor of `AdminController` doesn't accept these services, add them as constructor parameters instead of method parameters — match the existing style.)

- [ ] **Step 2: Build, commit**

```bash
dotnet build Humans.slnx -v quiet
git add src/Humans.Web/Controllers/AdminController.cs
git commit -m "feat(admin-shell): AdminController.Index populates dashboard model"
```

---

### Task 17: Replace `Admin/Index.cshtml` with skeleton dashboard

**Files:**
- Modify: `src/Humans.Web/Views/Admin/Index.cshtml`

- [ ] **Step 1: Replace contents**

```html
@model Humans.Web.Models.AdminDashboardViewModel
@{
    ViewData["Title"] = Localizer["AdminNav_Dashboard"];
}

<div class="page-head">
    <div>
        <h1 class="title">@Localizer["AdminDashboard_Greeting", Model.GreetingFirstName]</h1>
        <p class="sub">
            @Localizer["AdminDashboard_Subtitle",
                Model.ActiveHumans,
                Model.ShiftCoveragePercent,
                Model.OpenFeedback,
                Model.SystemAllNormal ? Localizer["AdminDashboard_AllSystemsNormal"].Value : Localizer["AdminDashboard_NeedsAttention"].Value]
        </p>
    </div>
</div>

<partial name="_DashboardStats" model="Model" />

<partial name="_DashboardStaffing" model="Model" />

<div class="split-panels">
    <div class="card">
        <div class="card-head">
            <h3>@Localizer["AdminDashboard_RecentActivity"]</h3>
            <small>@Localizer["AdminDashboard_RecentActivity_Caption"]</small>
        </div>
        <partial name="_DashboardActivity" model="Model.RecentActivity" />
    </div>
</div>
```

- [ ] **Step 2: Build, commit (the partials are stubs; we add them in Task 18-20)**

```bash
git add src/Humans.Web/Views/Admin/Index.cshtml
git commit -m "feat(admin-shell): replace Admin/Index with dashboard skeleton"
```

---

### Task 18: `_DashboardStats.cshtml`

**Files:**
- Create: `src/Humans.Web/Views/Shared/_DashboardStats.cshtml`

- [ ] **Step 1: Create**

```html
@model Humans.Web.Models.AdminDashboardViewModel

<div class="stats">
    <div class="stat">
        <div class="label">@Localizer["AdminDashboard_Stat_ActiveHumans"]</div>
        <div class="value">@Model.ActiveHumans</div>
    </div>
    <div class="stat">
        <div class="label">@Localizer["AdminDashboard_Stat_Shifts"]</div>
        <div class="value">
            @if (Model.ShiftTotalOf.HasValue)
            {
                @Model.ShiftFilledOf <em>/ @Model.ShiftTotalOf</em>
            }
            else
            {
                <em>—</em>
            }
        </div>
        <div class="delta">@Model.ShiftCoveragePercent% @Localizer["AdminDashboard_Stat_Shifts_Caption"]</div>
    </div>
    <div class="stat">
        <div class="label">@Localizer["AdminDashboard_Stat_OpenFeedback"]</div>
        <div class="value">@Model.OpenFeedback</div>
    </div>
    <div class="stat">
        <div class="label">@Localizer["AdminDashboard_Stat_SystemHealth"]</div>
        <div class="value @(Model.SystemAllNormal ? "" : "text-danger")">
            @if (Model.SystemAllNormal)
            {
                @Localizer["AdminDashboard_AllSystemsNormal"]
            }
            else
            {
                @Model.ErrorsLast24h <em>err / @Model.FailedJobs jobs</em>
            }
        </div>
    </div>
</div>
```

- [ ] **Step 2: Build, commit**

```bash
git add src/Humans.Web/Views/Shared/_DashboardStats.cshtml
git commit -m "feat(admin-shell): _DashboardStats partial"
```

---

### Task 19: `_DashboardStaffing.cshtml`

**Files:**
- Create: `src/Humans.Web/Views/Shared/_DashboardStaffing.cshtml`

- [ ] **Step 1: Create**

```html
@model Humans.Web.Models.AdminDashboardViewModel

<div class="card">
    <div class="card-head">
        <h3>@Localizer["AdminDashboard_Staffing_Title"]</h3>
        <small>
            @if (Model.ShiftTotalOf.HasValue)
            {
                @Localizer["AdminDashboard_Staffing_Caption", Model.ShiftFilledOf!, Model.ShiftTotalOf!]
            }
        </small>
    </div>
    <div class="staffing">
        @if (Model.StaffingByDepartment.Count == 0)
        {
            <p class="text-muted px-3">
                @Localizer["AdminDashboard_Staffing_Placeholder"]
                <a asp-controller="Vol" asp-action="Index">@Localizer["AdminNav_Volunteers"]</a>.
            </p>
        }
        else
        {
            foreach (var dept in Model.StaffingByDepartment)
            {
                var pct = (int)(dept.Ratio * 100);
                <div class="dept-row">
                    <span class="name">@dept.Name</span>
                    <div class="track @dept.TrackClass"><div style="width:@(pct)%"></div></div>
                    <span class="num">@pct%</span>
                </div>
            }
        }
    </div>
</div>
```

- [ ] **Step 2: Build, commit**

```bash
git add src/Humans.Web/Views/Shared/_DashboardStaffing.cshtml
git commit -m "feat(admin-shell): _DashboardStaffing partial (placeholder + real-data branch)"
```

---

### Task 20: `_DashboardActivity.cshtml`

**Files:**
- Create: `src/Humans.Web/Views/Shared/_DashboardActivity.cshtml`

- [ ] **Step 1: Create**

```html
@using Humans.Domain.Entities
@using Humans.Domain.Enums
@model IReadOnlyList<AuditLogEntry>

@if (Model.Count == 0)
{
    <p class="text-muted px-3 py-3">@Localizer["AdminDashboard_RecentActivity_Empty"]</p>
}
else
{
    foreach (var entry in Model)
    {
        var bubble = entry.Action switch
        {
            AuditAction.ApplicationApproved or AuditAction.ApplicationRejected => "✓",
            AuditAction.RoleAssigned or AuditAction.RoleRevoked => "🔑",
            AuditAction.VoteRecorded => "📜",
            AuditAction.GoogleSync => "↻",
            _ => "·"
        };
        <div class="activity-item">
            <div class="bubble">@bubble</div>
            <div>
                <strong>@entry.Action</strong>
                <p>@entry.Description</p>
            </div>
            <span class="time">@entry.OccurredAt.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)</span>
        </div>
    }
}
```

(Adjust the `AuditAction` enum cases to match the actual values in `Humans.Domain.Enums.AuditAction` — grep the enum to confirm names.)

- [ ] **Step 2: Smoke test**

```bash
dotnet build Humans.slnx -v quiet
dotnet run --project src/Humans.Web
```

Visit `/Admin` as Admin user. Expect: dashboard renders with all four stat tiles, a staffing card with placeholder text, and an activity feed showing the latest 8 audit entries (or "no recent activity" if the DB is empty).

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/Views/Shared/_DashboardActivity.cshtml
git commit -m "feat(admin-shell): _DashboardActivity partial"
```

---

## Phase 7 — Migrate other admin folders to `_AdminLayout`

### Task 21: Add `_ViewStart.cshtml` in pure-admin view folders

**Files (create one `_ViewStart.cshtml` per folder):**
- `src/Humans.Web/Views/Board/_ViewStart.cshtml`
- `src/Humans.Web/Views/Finance/_ViewStart.cshtml`
- `src/Humans.Web/Views/Google/_ViewStart.cshtml`
- `src/Humans.Web/Views/OnboardingReview/_ViewStart.cshtml`
- `src/Humans.Web/Views/Scanner/_ViewStart.cshtml`
- `src/Humans.Web/Views/Ticket/_ViewStart.cshtml`
- `src/Humans.Web/Views/AdminMerge/_ViewStart.cshtml`
- `src/Humans.Web/Views/AdminDuplicateAccounts/_ViewStart.cshtml`
- `src/Humans.Web/Views/AdminLegalDocuments/_ViewStart.cshtml`

- [ ] **Step 1: For each path above, create the file with this content**

```cshtml
@{
    Layout = "_AdminLayout";
}
```

- [ ] **Step 2: Build, smoke test each section**

```bash
dotnet build Humans.slnx -v quiet
dotnet run --project src/Humans.Web
```

For each affected route, sign in with the appropriate role and confirm the page renders inside the admin shell:

- `/Board` → `Board` role
- `/Finance` → `FinanceAdmin` role
- `/Google` → `Admin` role
- `/OnboardingReview` → `ConsentCoordinator` or `VolunteerCoordinator` role
- `/Scanner` → `TicketAdmin` role
- `/Tickets` → `TicketAdmin` role
- `/AdminMerge`, `/AdminDuplicateAccounts`, `/AdminLegalDocuments` → `Admin` role

Active state in the sidebar should highlight the corresponding item; breadcrumb should show `Admin / <Group> / <Item>`.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/Views/{Board,Finance,Google,OnboardingReview,Scanner,Ticket,AdminMerge,AdminDuplicateAccounts,AdminLegalDocuments}/_ViewStart.cshtml
git commit -m "feat(admin-shell): opt 9 admin folders into _AdminLayout"
```

---

### Task 22: Set per-view `Layout` for the six admin-side `Profile/` views

**Files:**
- Modify: `src/Humans.Web/Views/Profile/AdminList.cshtml`
- Modify: `src/Humans.Web/Views/Profile/AdminDetail.cshtml`
- Modify: `src/Humans.Web/Views/Profile/Search.cshtml`
- Modify: `src/Humans.Web/Views/Profile/SendMessage.cshtml`
- Modify: `src/Humans.Web/Views/Profile/Outbox.cshtml`
- Modify: `src/Humans.Web/Views/Profile/AddRole.cshtml`

- [ ] **Step 1: For each file above, add at the very top (before any other `@`-block)**

```cshtml
@{
    Layout = "_AdminLayout";
}
```

If the file already has a `@{ ... }` block at the top with `ViewData["Title"]`, merge `Layout = "_AdminLayout";` into the same block instead of creating a second one.

- [ ] **Step 2: Build and smoke test**

```bash
dotnet build Humans.slnx -v quiet
dotnet run --project src/Humans.Web
```

Sign in as `HumanAdmin`; visit `/Profile/AdminList`. Expect: admin shell layout. Then visit `/Profile/Edit` (member-side); expect: the original `_Layout`. The split is per-view, not folder-wide.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/Views/Profile/{AdminList,AdminDetail,Search,SendMessage,Outbox,AddRole}.cshtml
git commit -m "feat(admin-shell): admin-side Profile views opt into _AdminLayout per-view"
```

---

### Task 23: Migrate `Vol/` to `_AdminLayout` and delete `_VolLayout.cshtml`

**Files:**
- Modify: `src/Humans.Web/Views/Vol/_ViewStart.cshtml`
- Delete: `src/Humans.Web/Views/Vol/_VolLayout.cshtml`

- [ ] **Step 1: Read the existing `_VolLayout.cshtml` to identify section-specific UI**

```bash
cat src/Humans.Web/Views/Vol/_VolLayout.cshtml
```

If `_VolLayout` carries section-specific UI beyond the page chrome (e.g., a sub-nav for departments, a context-strip showing the active event), port that markup as a `_VolSubNav.cshtml` partial and have each top-level Vol view render it explicitly. If it's only chrome (header, footer), no port is needed.

- [ ] **Step 2: Update `Vol/_ViewStart.cshtml`**

```cshtml
@{
    Layout = "_AdminLayout";
}
```

- [ ] **Step 3: Delete `_VolLayout.cshtml`**

```bash
git rm src/Humans.Web/Views/Vol/_VolLayout.cshtml
```

- [ ] **Step 4: Build, smoke test**

```bash
dotnet build Humans.slnx -v quiet
dotnet run --project src/Humans.Web
```

Sign in as a `VolunteerCoordinator`; visit `/Vol`. Expect: admin shell, sidebar highlighting `Volunteers`, all interior pages render correctly. If a port from step 1 was needed and isn't yet placed, fix that before committing.

- [ ] **Step 5: Commit**

```bash
git add src/Humans.Web/Views/Vol/_ViewStart.cshtml
git commit -m "feat(admin-shell): Vol section joins admin shell, _VolLayout removed"
```

---

## Phase 8 — Top-nav surgery

### Task 24: Remove the 9 dark-orange `<li>` items and add the single `Admin` link

**Files:**
- Modify: `src/Humans.Web/Views/Shared/_Layout.cshtml`

- [ ] **Step 1: Remove the dark-orange items**

Open `_Layout.cshtml`. Delete lines that match `<li class="nav-item" authorize-policy="...">` for the following nine entries (lines 113-145 in the current file — verify before deleting):

- `VolunteerSectionAccess` (Vol, label "V")
- `ReviewQueueAccess` (Review)
- `BoardOrAdmin` "Voting" (`OnboardingReview/BoardVoting`)
- `BoardOrAdmin` "Board" (`Board/Index`)
- `HumanAdminOnly` (Humans → `Profile/AdminList`)
- `AdminOnly` (Admin → `Admin/Index`) — keep but rewrite per Step 2
- `AdminOnly` (Google)
- `TicketAdminBoardOrAdmin` (Tickets, Scanner) — both
- `FinanceAdminOrAdmin` (Finance)

- [ ] **Step 2: Replace the existing `Admin` link with a single composite-gated `Admin` link**

In place of the now-removed `<li>` block (or just before `</ul>`):

```html
@{
    var hasAnyAdminRole =
        User.IsInRole(Humans.Domain.Constants.RoleNames.Admin)
     || User.IsInRole(Humans.Domain.Constants.RoleNames.HumanAdmin)
     || User.IsInRole(Humans.Domain.Constants.RoleNames.TeamsAdmin)
     || User.IsInRole(Humans.Domain.Constants.RoleNames.CampAdmin)
     || User.IsInRole(Humans.Domain.Constants.RoleNames.TicketAdmin)
     || User.IsInRole(Humans.Domain.Constants.RoleNames.FeedbackAdmin)
     || User.IsInRole(Humans.Domain.Constants.RoleNames.FinanceAdmin)
     || User.IsInRole(Humans.Domain.Constants.RoleNames.NoInfoAdmin)
     || User.IsInRole(Humans.Domain.Constants.RoleNames.Board)
     || User.IsInRole(Humans.Domain.Constants.RoleNames.VolunteerCoordinator)
     || User.IsInRole(Humans.Domain.Constants.RoleNames.ConsentCoordinator);
}
@if (hasAnyAdminRole)
{
    <li class="nav-item">
        <a class="nav-link nav-restricted" asp-area="" asp-controller="Admin" asp-action="Index">@Localizer["Nav_Admin"]</a>
    </li>
}
```

The `nav-restricted` amber color is intentionally kept for now — the design says member-nav stays on current Bootstrap styling, and the single `Admin` link should still visually signal "elevated."

- [ ] **Step 3: Build, smoke test BOTH role contexts**

```bash
dotnet build Humans.slnx -v quiet
dotnet run --project src/Humans.Web
```

- As a member with no admin roles: top nav has Home, Camps, Teams, Calendar, City, Shifts, Budget — and **no** Admin link.
- As an Admin: same items plus a single `Admin` link at the end. Click it → `/Admin` opens in the admin shell. The dark-orange Review/Voting/Board/Humans/Vol/Tickets/Scanner/Finance/Google links are gone from the top nav.
- The admin shell sidebar is the only path to those nine destinations — verified.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/Views/Shared/_Layout.cshtml
git commit -m "feat(admin-shell): single Admin top-nav link replaces 9 dark-orange entries"
```

---

## Phase 9 — Cleanup + finalization

### Task 25: Drop the unused `.nav-restricted` rule from `site.css`

**Files:**
- Modify: `src/Humans.Web/wwwroot/css/site.css`

- [ ] **Step 1: Remove the rule**

Delete `site.css` lines 528-530 (`.navbar .nav-link.nav-restricted { color: var(--h-amber) !important; }`). The single `Admin` link in `_Layout.cshtml` still uses the class, so the rule must stay — REVISE: keep the rule, only remove if visual confirms the amber color is still desirable for the single link. If you want the `Admin` top-nav link to look like every other member nav link instead, drop both the class and the CSS rule.

- [ ] **Step 2: Build, commit if changed**

```bash
dotnet build Humans.slnx -v quiet
git add src/Humans.Web/wwwroot/css/site.css
git commit -m "chore(admin-shell): keep nav-restricted rule for single Admin link"
```

(If nothing changed, skip the commit.)

---

### Task 26: Localization keys

**Files:**
- Modify: `src/Humans.Web/Resources/SharedResources.en.resx` (and any other existing locale `.resx` files for parity)

- [ ] **Step 1: Add the following keys (English values shown; other locales can be filled by translators or copy English temporarily)**

| Key | Value |
|---|---|
| `AdminGroup_Operations` | Operations |
| `AdminGroup_Members` | Members |
| `AdminGroup_Money` | Money |
| `AdminGroup_Governance` | Governance |
| `AdminGroup_Integrations` | Integrations |
| `AdminGroup_PeopleData` | People data |
| `AdminGroup_Diagnostics` | Diagnostics |
| `AdminGroup_Dev` | Dev |
| `AdminNav_Brand` | Admin |
| `AdminNav_BrandSubtitle` | Admin |
| `AdminNav_Dashboard` | Dashboard |
| `AdminNav_Volunteers` | Volunteers |
| `AdminNav_Tickets` | Tickets |
| `AdminNav_Scanner` | Scanner |
| `AdminNav_Humans` | Humans |
| `AdminNav_Review` | Review |
| `AdminNav_Finance` | Finance |
| `AdminNav_Voting` | Voting |
| `AdminNav_Board` | Board |
| `AdminNav_Google` | Google |
| `AdminNav_EmailPreview` | Email preview |
| `AdminNav_EmailOutbox` | Email outbox |
| `AdminNav_Campaigns` | Campaigns |
| `AdminNav_WorkspaceAccounts` | Workspace accounts |
| `AdminNav_Merge` | Merge requests |
| `AdminNav_Duplicates` | Duplicate detection |
| `AdminNav_Audience` | Audience segmentation |
| `AdminNav_LegalDocuments` | Legal documents |
| `AdminNav_Logs` | Logs |
| `AdminNav_DbStats` | DB stats |
| `AdminNav_CacheStats` | Cache stats |
| `AdminNav_Configuration` | Configuration |
| `AdminNav_Hangfire` | Hangfire |
| `AdminNav_Health` | Health |
| `AdminNav_SeedBudget` | Seed budget |
| `AdminNav_SeedCampRoles` | Seed camp roles |
| `AdminNav_OpenSidebar` | Open admin menu |
| `AdminDashboard_Greeting` | Welcome back, {0}. |
| `AdminDashboard_Subtitle` | {0} active humans · {1}% shift coverage · {2} open feedback · {3} |
| `AdminDashboard_AllSystemsNormal` | all systems normal |
| `AdminDashboard_NeedsAttention` | needs attention |
| `AdminDashboard_RecentActivity` | Recent activity |
| `AdminDashboard_RecentActivity_Caption` | last 24h |
| `AdminDashboard_RecentActivity_Empty` | No recent activity. |
| `AdminDashboard_Stat_ActiveHumans` | Active humans |
| `AdminDashboard_Stat_Shifts` | Shifts staffed |
| `AdminDashboard_Stat_Shifts_Caption` | of slots filled |
| `AdminDashboard_Stat_OpenFeedback` | Open feedback |
| `AdminDashboard_Stat_SystemHealth` | System health |
| `AdminDashboard_Staffing_Title` | Staffing by department |
| `AdminDashboard_Staffing_Caption` | event-wide · {0} / {1} shifts |
| `AdminDashboard_Staffing_Placeholder` | Live staffing tile coming — see |

- [ ] **Step 2: Build, smoke test all keys resolve (no [Missing-Resource] markers)**

```bash
dotnet build Humans.slnx -v quiet
dotnet run --project src/Humans.Web
```

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/Resources/*.resx
git commit -m "feat(admin-shell): localization keys for sidebar groups, items, and dashboard"
```

---

### Task 27: Section invariant doc

**Files:**
- Create: `docs/sections/admin-shell.md`

- [ ] **Step 1: Copy the template and fill it**

```bash
cp docs/sections/SECTION-TEMPLATE.md docs/sections/admin-shell.md
```

Fill the sections per the template's headings. Key content for the Admin Shell:

- **Concepts:** Admin shell, sidebar, breadcrumb, dashboard skeleton.
- **Data Model:** None — Admin shell is a frame, not a data owner. Add a single line to `## Data Model`: "This section owns no entities."
- **Actors & Roles:** Admin, Board, HumanAdmin, FinanceAdmin, TicketAdmin, TeamsAdmin, CampAdmin, FeedbackAdmin, NoInfoAdmin, VolunteerCoordinator, ConsentCoordinator (all roles eligible to see the `Admin` top-nav link).
- **Invariants:**
  1. The `Admin` top-nav link is visible only to users with at least one admin-shaped role.
  2. Sidebar items are filtered per-item by `IAuthorizationService`; an item the user can't see does not appear.
  3. Sidebar groups whose visible items list is empty disappear.
  4. The admin shell adds no new authorization policies; it reuses existing `PolicyNames.*`.
  5. The `body.admin-shell` class scopes ALL admin-shell CSS — no styles bleed into member pages.
- **Negative Access Rules:** A user with no admin roles cannot reach `/Admin` (existing `[Authorize(Policy = AdminOnly)]` still applies).
- **Triggers:** None (no DB writes from this section).
- **Cross-Section Dependencies:** Calls `IProfileService`, `IShiftManagementService`, `IFeedbackService`, `IAuditLogService`, `IOnboardingService`, `IAdminDashboardService`. Reads only — no writes.
- **Architecture:** Frame-only section. Owning service: none. Owned tables: none. Migration status: A (greenfield).

- [ ] **Step 2: Commit**

```bash
git add docs/sections/admin-shell.md
git commit -m "docs(admin-shell): section invariant doc"
```

---

### Task 28: Update `docs/architecture/data-model.md` index

**Files:**
- Modify: `docs/architecture/data-model.md`

- [ ] **Step 1: Add a one-line note to the cross-cutting index**

Find the index block at the top of `data-model.md` (a list of sections + their owned entities) and add:

```markdown
- **Admin Shell** — frame only, no entities. See `docs/sections/admin-shell.md`.
```

- [ ] **Step 2: Commit**

```bash
git add docs/architecture/data-model.md
git commit -m "docs(admin-shell): mention Admin Shell as frame-only section in data-model index"
```

---

## Self-Review Checklist (run after the plan is committed and before handing off to the executor)

1. **Spec coverage:**
   - Top-nav surgery → Task 24 ✓
   - `_AdminLayout.cshtml` → Tasks 4, 9 ✓
   - `tokens.css` + `admin-shell.css` → Tasks 1, 8 ✓
   - Mobile offcanvas → Tasks 8, 9 ✓
   - `AdminSidebar` ViewComponent → Tasks 5, 6 ✓
   - `AdminBreadcrumb` ViewComponent → Task 7 ✓
   - Configured nav tree (8 groups, 25 items) → Task 3 ✓
   - Sidebar footer (avatar + role + dropdown) → Task 9 ✓
   - Dashboard skeleton (4 stats + activity + staffing) → Tasks 15-20 ✓
   - Service surface (`IProfileService.GetActiveApprovedCountAsync`, `IAdminDashboardService`, optional shift coverage) → Tasks 10-14 ✓
   - View-folder migration (10 admin folders + Profile mixed + Vol) → Tasks 21-23 ✓
   - Localization keys → Task 26 ✓
   - Section invariant doc → Tasks 27-28 ✓

2. **Placeholder scan:** No "TBD" / "TODO" except the deliberate `// TODO` in Task 14's log-store fallback. Steps that say "if absent" (Tasks 13, 14) are real conditionals on observed codebase state, not placeholders.

3. **Type consistency:**
   - `AdminNavItem` shape used in Task 2 matches usage in Task 3.
   - `AdminSidebarItemViewModel` shape from Task 6 matches the rendering in Task 9.
   - `AdminDashboardViewModel` from Task 15 matches the controller in Task 16 and views in 17-20.
   - Method names: `GetActiveApprovedCountAsync`, `GetActionableCountAsync`, `GetRecentAsync`, `GetSystemHealthAsync`, `GetOverallCoverageAsync` consistent across tasks.
