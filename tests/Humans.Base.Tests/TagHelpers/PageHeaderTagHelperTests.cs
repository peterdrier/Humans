using AwesomeAssertions;
using Humans.Base.TagHelpers;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Humans.Base.Tests.TagHelpers;

public class PageHeaderTagHelperTests
{
    private static async Task<string> RenderAsync(
        PageHeaderTagHelper helper, string? subtitleSlot = null, string actions = "",
        string? titleSlot = null, string? badgeSlot = null)
    {
        var items = new Dictionary<object, object>();
        var context = new TagHelperContext("page-header", [], items, Guid.NewGuid().ToString("N"));
        var output = new TagHelperOutput("page-header", [], async (_, _) =>
        {
            var child = new DefaultTagHelperContent();
            await AppendSlotAsync(child, items, "page-header-title", new PageHeaderTitleTagHelper(), titleSlot);
            await AppendSlotAsync(child, items, "page-header-subtitle", new PageHeaderSubtitleTagHelper(), subtitleSlot);
            await AppendSlotAsync(child, items, "page-header-badge", new PageHeaderBadgeTagHelper(), badgeSlot);
            child.AppendHtml(actions);
            return child;
        });

        await helper.ProcessAsync(context, output);
        return output.Content.GetContent();
    }

    private static async Task AppendSlotAsync(
        DefaultTagHelperContent parent, Dictionary<object, object> items, string tag, TagHelper slot, string? html)
    {
        if (html is null)
        {
            return;
        }
        var slotContext = new TagHelperContext(tag, [], items, Guid.NewGuid().ToString("N"));
        var slotOutput = new TagHelperOutput(tag, [], (_, _) =>
        {
            var c = new DefaultTagHelperContent();
            c.AppendHtml(html);
            return Task.FromResult<TagHelperContent>(c);
        });
        await slot.ProcessAsync(slotContext, slotOutput);
        parent.AppendHtml(slotOutput.Content.GetContent());
    }

    [HumansFact]
    public async Task NullTitle_RendersEmptyHeadingInsteadOfThrowing()
    {
        var html = await RenderAsync(new PageHeaderTagHelper { Title = null! });

        html.Should().Be("<div class=\"mb-4\"><h1 class=\"mb-0\"></h1></div>");
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

    [HumansFact]
    public async Task TitleSlot_RendersMarkupInsideTheHeadingAndWinsOverAttribute()
    {
        var html = await RenderAsync(
            new PageHeaderTagHelper { Title = "ignored" },
            titleSlot: "<i class=\"fa-solid fa-car me-2\"></i>Rideshare <small>x</small>");

        html.Should().Be(
            "<div class=\"mb-4\"><h1 class=\"mb-0\"><i class=\"fa-solid fa-car me-2\"></i>Rideshare <small>x</small></h1></div>");
    }

    [HumansFact]
    public async Task BadgeSlot_TakesTheAttributeBadgePosition()
    {
        var html = await RenderAsync(
            new PageHeaderTagHelper { Title = "T", Badge = "ignored" },
            badgeSlot: "<span class=\"badge bg-success fs-6\">Open</span>");

        html.Should().Be(
            "<div class=\"mb-4\"><div class=\"d-flex align-items-center gap-2\"><h1 class=\"mb-0\">T</h1>"
            + "<span class=\"badge bg-success fs-6\">Open</span></div></div>");
    }

    [HumansFact]
    public async Task EmptyBadgeSlot_RendersNoBadgeWrapper()
    {
        var html = await RenderAsync(new PageHeaderTagHelper { Title = "T" }, badgeSlot: "");

        html.Should().Be("<div class=\"mb-4\"><h1 class=\"mb-0\">T</h1></div>");
    }

    [HumansFact]
    public async Task Compact_RendersAdminShellPageHeadWithBottomAlignedActions()
    {
        var html = await RenderAsync(
            new PageHeaderTagHelper { Title = "Database", Subtitle = "db · 16", Variant = "compact" },
            actions: "<a class=\"btn\">Admin</a>");

        html.Should().Be(
            "<div class=\"page-head\"><div><h1 class=\"title\">Database</h1><p class=\"sub\">db &#xB7; 16</p></div>"
            + "<div class=\"d-flex align-items-center gap-3\"><a class=\"btn\">Admin</a></div></div>");
    }

    [HumansFact]
    public async Task Compact_WithoutActionsOrSubtitle_RendersTitleOnly()
    {
        var html = await RenderAsync(
            new PageHeaderTagHelper { Title = "Store summary", Variant = "compact" }, subtitleSlot: "");

        html.Should().Be("<div class=\"page-head\"><div><h1 class=\"title\">Store summary</h1></div></div>");
    }

    [HumansFact]
    public async Task Compact_SubtitleSlot_KeepsTheSubClass()
    {
        var html = await RenderAsync(
            new PageHeaderTagHelper { Title = "T", Variant = "compact" }, subtitleSlot: "<a href=\"/x\">x</a>");

        html.Should().Contain("<div class=\"sub\"><a href=\"/x\">x</a></div>");
    }
}
