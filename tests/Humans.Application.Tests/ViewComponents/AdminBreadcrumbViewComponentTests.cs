using AwesomeAssertions;
using Humans.Web.ViewComponents;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;

namespace Humans.Application.Tests.ViewComponents;

public class AdminBreadcrumbViewComponentTests
{
    [HumansFact]
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

    [HumansFact]
    public void Falls_Back_To_PageTitle_For_Unknown_Controller()
    {
        var sut = new AdminBreadcrumbViewComponent();
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
        model!.GroupLabelKey.Should().BeNull();
        model.ItemLabelKey.Should().BeNull();
        model.FallbackTitle.Should().Be("Some Page");
    }
}
