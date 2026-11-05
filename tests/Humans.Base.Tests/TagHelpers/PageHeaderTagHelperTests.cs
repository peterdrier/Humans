using AwesomeAssertions;
using Humans.Base.TagHelpers;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Humans.Base.Tests.TagHelpers;

public class PageHeaderTagHelperTests
{
    private static async Task<string> RenderAsync(
        PageHeaderTagHelper helper, string? subtitleSlot = null, string actions = "")
    {
        var items = new Dictionary<object, object>();
        var context = new TagHelperContext("page-header", [], items, Guid.NewGuid().ToString("N"));
        var output = new TagHelperOutput("page-header", [], async (_, _) =>
        {
            var child = new DefaultTagHelperContent();
            if (subtitleSlot is not null)
            {
                var slotContext = new TagHelperContext("page-header-subtitle", [], items, Guid.NewGuid().ToString("N"));
                var slotOutput = new TagHelperOutput("page-header-subtitle", [], (_, _) =>
                {
                    var c = new DefaultTagHelperContent();
                    c.AppendHtml(subtitleSlot);
                    return Task.FromResult<TagHelperContent>(c);
                });
                await new PageHeaderSubtitleTagHelper().ProcessAsync(slotContext, slotOutput);
                child.AppendHtml(slotOutput.Content.GetContent());
            }
            child.AppendHtml(actions);
            return child;
        });

        await helper.ProcessAsync(context, output);
        return output.Content.GetContent();
    }

    [HumansFact]
    public async Task Plain_RendersTitleOnlyInBottomMarginBand()
    {
        var html = await RenderAsync(new PageHeaderTagHelper { Title = "Finance" });

        html.Should().Be("<div class=\"mb-4\"><h1 class=\"mb-0\">Finance</h1></div>");
    }

    [HumansFact]
    public async Task SubtitleAttribute_StaysEncodedPlainText()
    {
        var html = await RenderAsync(new PageHeaderTagHelper { Title = "T", Subtitle = "<b>x</b>" });

        html.Should().Contain("<p class=\"text-muted mb-0\">&lt;b&gt;x&lt;/b&gt;</p>");
    }

    [HumansFact]
    public async Task SubtitleSlot_RendersMarkupAndWinsOverAttribute()
    {
        var html = await RenderAsync(
            new PageHeaderTagHelper { Title = "T", Subtitle = "ignored" },
            subtitleSlot: "<a href=\"/t\">Team</a>");

        html.Should().Be(
            "<div class=\"mb-4\"><h1 class=\"mb-0\">T</h1>"
            + "<div class=\"text-muted mb-0\"><a href=\"/t\">Team</a></div></div>");
    }

    [HumansFact]
    public async Task SubtitleSlot_IsNotLeakedIntoTheActionsArea()
    {
        var html = await RenderAsync(
            new PageHeaderTagHelper { Title = "T" },
            subtitleSlot: "sub",
            actions: "<a class=\"btn\">Go</a>");

        html.Should().Contain("<div class=\"d-flex flex-wrap gap-2\"><a class=\"btn\">Go</a></div>");
        html.Split("sub").Length.Should().Be(2, "the subtitle renders once, under the title");
    }

    [HumansFact]
    public async Task Actions_RenderRightAlignedBesideTheTitle()
    {
        var html = await RenderAsync(new PageHeaderTagHelper { Title = "T" }, actions: "<a class=\"btn\">Go</a>");

        html.Should().Be(
            "<div class=\"d-flex flex-wrap justify-content-between align-items-center gap-3 mb-4\">"
            + "<div><h1 class=\"mb-0\">T</h1></div>"
            + "<div class=\"d-flex flex-wrap gap-2\"><a class=\"btn\">Go</a></div></div>");
    }

    [HumansFact]
    public async Task Badge_SitsBesideTheHeadingNotInsideIt()
    {
        var html = await RenderAsync(new PageHeaderTagHelper { Title = "T", Badge = "Open", BadgeClass = "bg-success" });

        html.Should().Contain("<h1 class=\"mb-0\">T</h1><span class=\"badge bg-success\">Open</span>");
    }
}
