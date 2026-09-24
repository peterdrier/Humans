using AwesomeAssertions;
using Humans.Base.Interfaces;
using Humans.Base.ViewComponents;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;

namespace Humans.Base.Tests.ViewComponents;

public class AdminBreadcrumbViewComponentTests
{
    /// <summary>
    /// A minimal stand-in for the real, section-contributed nav (nobodies-collective/Humans#1077):
    /// one item per group this file's assertions need to resolve.
    /// </summary>
    private sealed class FakeNav : ISectionAdminNav
    {
        public IEnumerable<AdminNavGroup> Groups() =>
        [
            new("Tickets", [
                new("Tickets", "Ticket", "Index", null, null, "icon", null)
            ]),
            new("Debug", [
                new("Logs",     "Debug", "Logs",    null, null, "icon", null),
                new("DB stats", "Debug", "DbStats", null, null, "icon", null)
            ])
        ];
    }

    private static AdminBreadcrumbViewModel Crumb(
        string controller, string action, string? title = null, string? parent = null, IHtmlContent? trail = null,
        ISectionAdminNav? nav = null)
    {
        var viewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary())
        {
            ["Title"] = title,
            [AdminNavComposition.ParentKey] = parent
        };
        var sut = new AdminBreadcrumbViewComponent(nav is null ? [] : [nav])
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new Microsoft.AspNetCore.Mvc.Rendering.ViewContext
                {
                    RouteData = new RouteData { Values = { ["controller"] = controller, ["action"] = action } },
                    ViewData = viewData
                }
            }
        };
        var result = sut.Invoke(trail) as ViewViewComponentResult;
        return (AdminBreadcrumbViewModel)result!.ViewData!.Model!;
    }

    [HumansFact]
    public void Nav_Page_Is_Group_Then_Page()
    {
        var model = Crumb("Debug", "Logs", title: "Application Logs", nav: new FakeNav());

        model.Group.Should().Be("Debug");
        model.Page!.Label.Should().Be("Logs");
        model.IsSubpage.Should().BeFalse();
        model.PageRepeatsGroup.Should().BeFalse();
    }

    [HumansFact]
    public void Disambiguates_Items_That_Share_A_Controller_By_Action()
    {
        // Regression: DebugController has several sidebar items; a controller-only match
        // returned the first one regardless of action.
        Crumb("Debug", "DbStats", nav: new FakeNav()).Page!.Label.Should().Be("DB stats");
    }

    [HumansFact]
    public void A_Page_Named_Like_Its_Group_States_The_Label_Once()
    {
        Crumb("Ticket", "Index", nav: new FakeNav()).PageRepeatsGroup.Should().BeTrue();
    }

    [HumansFact]
    public void Subpage_Is_Group_Page_Then_Its_Title_Or_Trail()
    {
        var trail = new HtmlString("<a href=\"/x\">Order 42</a>");
        var model = Crumb("Ticket", "Orders", title: "Ticket Orders", trail: trail, nav: new FakeNav());

        model.Group.Should().Be("Tickets");
        model.Page!.Label.Should().Be("Tickets");
        model.IsSubpage.Should().BeTrue();
        model.Trail.Should().BeSameAs(trail);
        model.Title.Should().Be("Ticket Orders");
    }

    [HumansFact]
    public void Subpage_Named_Parent_Wins_Over_The_First_Item_On_Its_Controller()
    {
        Crumb("Debug", "LogDetail", parent: "DbStats", nav: new FakeNav()).Page!.Label.Should().Be("DB stats");
    }

    [HumansFact]
    public void Falls_Back_To_PageTitle_For_Unknown_Controller()
    {
        var model = Crumb("Unknown", "Index", title: "Some Page");

        model.Group.Should().BeNull();
        model.Page.Should().BeNull();
        model.Title.Should().Be("Some Page");
    }
}
