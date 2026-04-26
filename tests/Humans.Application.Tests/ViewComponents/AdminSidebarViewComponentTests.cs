using System.Security.Claims;
using AwesomeAssertions;
using Humans.Domain.Constants;
using Humans.Web.Authorization;
using Humans.Web.ViewComponents;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using NSubstitute;

namespace Humans.Application.Tests.ViewComponents;

public class AdminSidebarViewComponentTests
{
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
        // Allow only items in the Operations group
        auth.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(),
                Arg.Is<string>(p => p == PolicyNames.VolunteerSectionAccess || p == PolicyNames.TicketAdminBoardOrAdmin))
            .Returns(AuthorizationResult.Success());
        auth.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(),
                Arg.Is<string>(p => p != PolicyNames.VolunteerSectionAccess && p != PolicyNames.TicketAdminBoardOrAdmin))
            .Returns(AuthorizationResult.Failed());
        var sut = MakeSut(auth, "Vol", "Index");
        var result = await sut.InvokeAsync() as ViewViewComponentResult;
        var model = result!.ViewData!.Model as AdminSidebarViewModel;
        model!.Groups.Should().HaveCount(1);
        model.Groups.Single().LabelKey.Should().Be("AdminGroup_Operations");
    }

    [HumansFact]
    public async Task Marks_Active_Item_From_RouteData()
    {
        var auth = AlwaysAllow();
        var sut = MakeSut(auth, "Ticket", "Index");
        var result = await sut.InvokeAsync() as ViewViewComponentResult;
        var model = result!.ViewData!.Model as AdminSidebarViewModel;
        var ticketsItem = model!.Groups.SelectMany(g => g.Items)
            .Single(i => string.Equals(i.LabelKey, "AdminNav_Tickets", StringComparison.Ordinal));
        ticketsItem.IsActive.Should().BeTrue();
        var volunteersItem = model.Groups.SelectMany(g => g.Items)
            .Single(i => string.Equals(i.LabelKey, "AdminNav_Volunteers", StringComparison.Ordinal));
        volunteersItem.IsActive.Should().BeFalse();
    }

    [HumansFact]
    public async Task Active_Item_Match_Requires_Both_Controller_And_Action()
    {
        // Regression: when on /Admin/Logs, only the Logs sidebar item should be active.
        // Previously a controller-only match made all 5 items under controller="Admin"
        // (Logs, DbStats, CacheStats, Configuration, AudienceSegmentation) light up.
        var auth = AlwaysAllow();
        var sut = MakeSut(auth, "Admin", "Logs");
        var result = await sut.InvokeAsync() as ViewViewComponentResult;
        var model = result!.ViewData!.Model as AdminSidebarViewModel;
        var allItems = model!.Groups.SelectMany(g => g.Items).ToList();
        var activeItems = allItems.Where(i => i.IsActive).ToList();
        activeItems.Should().HaveCount(1);
        activeItems.Single().LabelKey.Should().Be("AdminNav_Logs");
    }

    [HumansFact]
    public async Task Hides_Dev_Group_In_Production()
    {
        var env = Substitute.For<IWebHostEnvironment>();
        env.EnvironmentName.Returns("Production");
        var sut = MakeSut(AlwaysAllow(), "Home", "Index", env);
        var result = await sut.InvokeAsync() as ViewViewComponentResult;
        var model = result!.ViewData!.Model as AdminSidebarViewModel;
        model!.Groups.Should().NotContain(g => g.LabelKey == "AdminGroup_Dev");
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
        var sp = Substitute.For<IServiceProvider>();
        var http = Substitute.For<IHttpContextAccessor>();
        var sut = new AdminSidebarViewComponent(auth, env, sp, http);

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
