using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Authorization;
using Humans.Base.Interfaces;
using Humans.Web.ViewComponents;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Humans.Web.Tests.ViewComponents;

public class AdminSidebarViewComponentTests
{
    /// <summary>
    /// A minimal stand-in for the real, section-contributed nav (nobodies-collective/Humans#1077):
    /// just enough groups/items for this file's assertions — a group with two items (for
    /// active-item and empty-group filtering), a group whose items share a controller with
    /// different actions (for the controller+action match regression), and a prod-gated group
    /// (for the Development-hides-in-production case).
    /// </summary>
    private sealed class FakeNav : ISectionAdminNav
    {
        public IEnumerable<AdminNavGroup> Groups() =>
        [
            new("Tickets", [
                new("Tickets", "Ticket",   "Index", null, null, "icon", PolicyNames.TicketAdminBoardOrAdmin),
                new("Scanner", "Scanner",  "Index", null, null, "icon", PolicyNames.ScannerAccess)
            ]),
            new("Debug", [
                new("Logs",     "Debug", "Logs",    null, null, "icon", PolicyNames.AdminOnly),
                new("DB stats", "Debug", "DbStats", null, null, "icon", PolicyNames.AdminOnly)
            ]),
            new("Development", [
                new("Seed budget", "DevSeed", "SeedBudget", null, null, "icon", PolicyNames.AdminOnly,
                     EnvironmentGate: env => !env.IsProduction())
            ])
        ];
    }

    [HumansFact]
    public async Task Hides_Items_When_Authorization_Fails()
    {
        var auth = Substitute.For<IAuthorizationService>();
        auth.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(AuthorizationResult.Failed());
        var sut = MakeSut(auth, "Home", "Index");
        var result = await sut.InvokeAsync() as ViewViewComponentResult;
        var model = result!.ViewData!.Model as AdminSidebarViewModel;
        model!.Groups.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Hides_Empty_Groups()
    {
        var auth = Substitute.For<IAuthorizationService>();
        // Allow only items in the Tickets group
        auth.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(),
                Arg.Is<string>(p => p == PolicyNames.TicketAdminBoardOrAdmin))
            .Returns(AuthorizationResult.Success());
        auth.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(),
                Arg.Is<string>(p => p != PolicyNames.TicketAdminBoardOrAdmin))
            .Returns(AuthorizationResult.Failed());
        var sut = MakeSut(auth, "Ticket", "Index");
        var result = await sut.InvokeAsync() as ViewViewComponentResult;
        var model = result!.ViewData!.Model as AdminSidebarViewModel;
        model!.Groups.Should().HaveCount(1);
        model.Groups.Single().Label.Should().Be("Tickets");
    }

    [HumansFact]
    public async Task Marks_Active_Item_From_RouteData()
    {
        var auth = AlwaysAllow();
        var sut = MakeSut(auth, "Ticket", "Index");
        var result = await sut.InvokeAsync() as ViewViewComponentResult;
        var model = result!.ViewData!.Model as AdminSidebarViewModel;
        var ticketsItem = model!.Groups.SelectMany(g => g.Items)
            .Single(i => string.Equals(i.Label, "Tickets", StringComparison.Ordinal));
        ticketsItem.IsActive.Should().BeTrue();
        var scannerItem = model.Groups.SelectMany(g => g.Items)
            .Single(i => string.Equals(i.Label, "Scanner", StringComparison.Ordinal));
        scannerItem.IsActive.Should().BeFalse();
    }

    [HumansFact]
    public async Task Active_Item_Match_Requires_Both_Controller_And_Action()
    {
        // Regression: when on /Debug/Logs, only the Logs sidebar item should be active.
        // Previously a controller-only match made all diagnostics items under
        // controller="Debug" light up.
        var auth = AlwaysAllow();
        var sut = MakeSut(auth, "Debug", "Logs");
        var result = await sut.InvokeAsync() as ViewViewComponentResult;
        var model = result!.ViewData!.Model as AdminSidebarViewModel;
        var allItems = model!.Groups.SelectMany(g => g.Items).ToList();
        var activeItems = allItems.Where(i => i.IsActive).ToList();
        activeItems.Should().HaveCount(1);
        activeItems.Single().Label.Should().Be("Logs");
    }

    [HumansFact]
    public async Task Rows_Are_Alphabetical_And_Link_To_The_First_Visible_Item()
    {
        var sut = MakeSut(AlwaysAllow(), "Home", "Index");
        var result = await sut.InvokeAsync() as ViewViewComponentResult;
        var model = result!.ViewData!.Model as AdminSidebarViewModel;
        model!.Groups.Select(g => g.Label).Should().Equal("Debug", "Development", "Tickets");
        model.Groups.Single(g => string.Equals(g.Label, "Debug", StringComparison.Ordinal)).First.Action.Should().Be("Logs");
    }

    [HumansFact]
    public async Task Subpage_Marks_Its_Group_Row_Active()
    {
        // /Scanner/Barcode is no nav item; it belongs under Scanner by controller.
        var sut = MakeSut(AlwaysAllow(), "Scanner", "Barcode");
        var result = await sut.InvokeAsync() as ViewViewComponentResult;
        var model = result!.ViewData!.Model as AdminSidebarViewModel;
        model!.Groups.Where(g => g.IsActive).Select(g => g.Label).Should().Equal("Tickets");
    }

    [HumansFact]
    public async Task Hides_Development_Group_In_Production()
    {
        var env = Substitute.For<IWebHostEnvironment>();
        env.EnvironmentName.Returns("Production");
        var sut = MakeSut(AlwaysAllow(), "Home", "Index", env);
        var result = await sut.InvokeAsync() as ViewViewComponentResult;
        var model = result!.ViewData!.Model as AdminSidebarViewModel;
        model!.Groups.Should().NotContain(g => g.Label == "Development");
    }

    private static IAuthorizationService AlwaysAllow()
    {
        var auth = Substitute.For<IAuthorizationService>();
        auth.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(AuthorizationResult.Success());
        return auth;
    }

    private static AdminSidebarViewComponent MakeSut(
        IAuthorizationService auth, string controller, string action, IWebHostEnvironment? env = null)
    {
        env ??= MakeDevEnv();
        var sp = new ServiceLocatorBuilder().Build();
        var sut = new AdminSidebarViewComponent(auth, env, sp, [new FakeNav()], NullLogger<AdminSidebarViewComponent>.Instance);

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
        var env = Substitute.For<IWebHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        return env;
    }
}
