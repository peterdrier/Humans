using AwesomeAssertions;
using Humans.Base.ViewComponents;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace Humans.Base.Tests.ViewComponents;

public class BreadcrumbViewComponentTests
{
    private static List<string> Items(string? trail)
    {
        var sut = new BreadcrumbViewComponent
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext
                {
                    ViewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary())
                }
            }
        };
        var result = sut.Invoke(trail is null ? null : new HtmlString(trail)) as ViewViewComponentResult;
        var model = (BreadcrumbViewModel)result!.ViewData!.Model!;
        return [.. model.Items.Select(i =>
        {
            using var writer = new StringWriter();
            i.WriteTo(writer, System.Text.Encodings.Web.HtmlEncoder.Default);
            return writer.ToString();
        })];
    }

    [HumansFact]
    public void No_Trail_Yields_No_Items()
    {
        Items(null).Should().BeEmpty();
        Items("  \n  ").Should().BeEmpty();
    }

    [HumansFact]
    public void Each_Top_Level_Element_Is_One_Item_In_Order()
    {
        Items("""
            <a href="/teams">Teams</a>
            <a href="/teams/fire">Fire</a>
            <span>Edit</span>
            """)
            .Should().Equal("<a href=\"/teams\">Teams</a>", "<a href=\"/teams/fire\">Fire</a>", "Edit");
    }

    [HumansFact]
    public void A_Bare_Span_Contributes_Its_Content_But_A_Span_With_Attributes_Stays_Whole()
    {
        Items("<span><b>Ann</b> Lee</span><span class=\"x\">Here</span>")
            .Should().Equal("<b>Ann</b> Lee", "<span class=\"x\">Here</span>");
    }

    [HumansFact]
    public void Nested_Links_Do_Not_Split_An_Item()
    {
        Items("<span><a href=\"/u\">Ann</a> <i>(admin)</i></span>")
            .Should().Equal("<a href=\"/u\">Ann</a> <i>(admin)</i>");
    }
}
