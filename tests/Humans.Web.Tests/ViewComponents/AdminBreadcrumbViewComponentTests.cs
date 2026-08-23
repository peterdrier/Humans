using AwesomeAssertions;
using Humans.Base.Interfaces;
using Humans.Web.ViewComponents;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;

namespace Humans.Web.Tests.ViewComponents;

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
            new("Money", [
                new("Expense review", "Expenses", "Review", null, null, "icon", null,
                    BreadcrumbLabel: "Review")
            ]),
            new("Diagnostics", System: true, Items: [
                new("Logs",     "Debug", "Logs",    null, null, "icon", null),
                new("DB stats", "Debug", "DbStats", null, null, "icon", null)
            ])
        ];
    }

    /// <summary>
    /// The section crumb is the contributor's assembly name minus the "Humans." prefix — here the
    /// test assembly, since <see cref="FakeNav"/> lives in it. In the app that is "Expenses",
    /// "Budget", and so on, with no Shell-side table to keep in step.
    /// </summary>
    private const string FakeNavSection = "Web.Tests";

    [HumansFact]
    public void Resolves_Section_And_Item_For_Known_Controller()
    {
        var sut = new AdminBreadcrumbViewComponent([new FakeNav()]);
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
        model!.SectionLabel.Should().Be(FakeNavSection);
        model.ItemLabel.Should().Be("Tickets");
    }

    [HumansFact]
    public void Prefers_BreadcrumbLabel_Over_The_Sidebar_Label()
    {
        // The sidebar sits under a merge group ("Money") shared by four sections, so its label
        // repeats the section; the breadcrumb already states the section, so it uses the short form.
        var sut = new AdminBreadcrumbViewComponent([new FakeNav()]);
        var ctx = new ViewComponentContext
        {
            ViewContext = new Microsoft.AspNetCore.Mvc.Rendering.ViewContext
            {
                RouteData = new RouteData { Values = { ["controller"] = "Expenses", ["action"] = "Review" } }
            }
        };
        sut.ViewComponentContext = ctx;
        var result = sut.Invoke() as ViewViewComponentResult;
        var model = result!.ViewData!.Model as AdminBreadcrumbViewModel;
        model!.SectionLabel.Should().Be(FakeNavSection);
        model.ItemLabel.Should().Be("Review");
    }

    [HumansFact]
    public void Disambiguates_Items_That_Share_A_Controller_By_Action()
    {
        // Regression: DebugController has multiple sidebar items (Logs, DbStats,
        // CacheStats, Configuration, ClientStats). Matching by controller alone returned
        // the first one regardless of action. The breadcrumb must disambiguate by action.
        var sut = new AdminBreadcrumbViewComponent([new FakeNav()]);
        var ctx = new ViewComponentContext
        {
            ViewContext = new Microsoft.AspNetCore.Mvc.Rendering.ViewContext
            {
                RouteData = new RouteData { Values = { ["controller"] = "Debug", ["action"] = "DbStats" } }
            }
        };
        sut.ViewComponentContext = ctx;
        var result = sut.Invoke() as ViewViewComponentResult;
        var model = result!.ViewData!.Model as AdminBreadcrumbViewModel;
        model!.SectionLabel.Should().Be(FakeNavSection);
        model.ItemLabel.Should().Be("DB stats");
    }

    [HumansFact]
    public void Falls_Back_To_PageTitle_For_Unknown_Controller()
    {
        var sut = new AdminBreadcrumbViewComponent([]);
        var ctx = new ViewComponentContext
        {
            ViewContext = new Microsoft.AspNetCore.Mvc.Rendering.ViewContext
            {
                RouteData = new RouteData { Values = { ["controller"] = "Unknown", ["action"] = "Index" } },
                ViewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary())
                {
                    ["Title"] = "Some Page"
                }
            }
        };
        sut.ViewComponentContext = ctx;
        var result = sut.Invoke() as ViewViewComponentResult;
        var model = result!.ViewData!.Model as AdminBreadcrumbViewModel;
        model!.SectionLabel.Should().BeNull();
        model.ItemLabel.Should().BeNull();
        model.FallbackTitle.Should().Be("Some Page");
    }
}
