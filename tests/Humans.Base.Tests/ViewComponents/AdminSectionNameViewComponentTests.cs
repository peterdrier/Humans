using AwesomeAssertions;
using Humans.Base.Interfaces;
using Humans.Base.ViewComponents;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;

namespace Humans.Base.Tests.ViewComponents;

public class AdminSectionNameViewComponentTests
{
    private sealed class FakeNav : ISectionAdminNav
    {
        public IEnumerable<AdminNavGroup> Groups() =>
        [
            new("Debug", [
                new("Logs",     "Debug", "Logs",    null, null, "icon", null),
                new("DB stats", "Debug", "DbStats", null, null, "icon", null)
            ])
        ];
    }

    private static string Name(string controller, string action, string? title = null, ISectionAdminNav? nav = null)
    {
        var viewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary())
        {
            ["Title"] = title
        };
        var sut = new AdminSectionNameViewComponent(nav is null ? [] : [nav])
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
        return ((ContentViewComponentResult)sut.Invoke()).Content;
    }

    [HumansFact]
    public void Invoke_NavPage_IsGroupLabel()
    {
        Name("Debug", "DbStats", title: "Database Query Statistics", nav: new FakeNav()).Should().Be("Debug");
    }

    [HumansFact]
    public void Invoke_Subpage_IsItsGroupLabel()
    {
        Name("Debug", "LogDetail", title: "Log entry", nav: new FakeNav()).Should().Be("Debug");
    }

    [HumansFact]
    public void Invoke_OffNav_IsPageTitle()
    {
        Name("Unknown", "Index", title: "Some Page").Should().Be("Some Page");
    }
}
