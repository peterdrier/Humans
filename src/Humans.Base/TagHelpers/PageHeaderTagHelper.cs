using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Humans.Base.TagHelpers;

/// <summary>
/// Renders the standard page-header band: a single &lt;h1&gt; title (optionally with a
/// status badge beside it and a muted subtitle below) on the left, and a right-aligned
/// action area fed by the tag's child content.
///
/// Usage:
///   <page-header title="Finance">
///       <a asp-action="New" class="btn btn-primary">New year</a>
///   </page-header>
///
///   <page-header title="@Model.Name" subtitle="Budget year"
///                badge="@Model.Status" badge-class="@Model.Status.GetBadgeClass()" />
///
///   <page-header title="@Model.Name">
///       <page-header-subtitle><a asp-action="Team">@Model.TeamName</a></page-header-subtitle>
///       <a asp-action="Edit" class="btn btn-primary">Edit</a>
///   </page-header>
///
/// Always emits exactly one &lt;h1&gt; (so page titles stop drifting to h2) and keeps the
/// badge OUT of the heading text, so screen-reader heading outlines stay clean.
/// Title and badge are plain text. A subtitle that carries markup (links, sanitized markdown)
/// goes in a nested <c>&lt;page-header-subtitle&gt;</c>, which wins over the <c>subtitle</c>
/// attribute; every other child is the right-aligned actions area.
/// </summary>
[HtmlTargetElement("page-header", Attributes = "title")]
public class PageHeaderTagHelper : TagHelper
{
    /// <summary>The page title. Rendered as the page's single &lt;h1&gt;.</summary>
    [HtmlAttributeName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>Optional muted subtitle rendered below the title.</summary>
    [HtmlAttributeName("subtitle")]
    public string? Subtitle { get; set; }

    /// <summary>Optional badge text shown beside the title (kept outside the heading).</summary>
    [HtmlAttributeName("badge")]
    public string? Badge { get; set; }

    /// <summary>Bootstrap badge classes for <see cref="Badge"/>; defaults to <c>bg-secondary</c>.</summary>
    [HtmlAttributeName("badge-class")]
    public string? BadgeClass { get; set; }

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(output);

        var slot = new SubtitleSlot();
        context.Items[typeof(PageHeaderTagHelper)] = slot;
        var actions = (await output.GetChildContentAsync()).GetContent().Trim();
        var hasActions = !string.IsNullOrWhiteSpace(actions);
        var enc = HtmlEncoder.Default;

        var titleInner = new StringBuilder();
        var hasBadge = !string.IsNullOrWhiteSpace(Badge);
        if (hasBadge)
        {
            titleInner.Append("<div class=\"d-flex align-items-center gap-2\">");
        }
        titleInner.Append("<h1 class=\"mb-0\">").Append(enc.Encode(Title)).Append("</h1>");
        if (hasBadge)
        {
            var badgeClass = string.IsNullOrWhiteSpace(BadgeClass) ? "bg-secondary" : BadgeClass.Trim();
            titleInner.Append("<span class=\"badge ").Append(enc.Encode(badgeClass)).Append("\">")
                      .Append(enc.Encode(Badge!)).Append("</span></div>");
        }
        if (!string.IsNullOrWhiteSpace(slot.Html))
        {
            titleInner.Append("<div class=\"text-muted mb-0\">").Append(slot.Html).Append("</div>");
        }
        else if (!string.IsNullOrWhiteSpace(Subtitle))
        {
            titleInner.Append("<p class=\"text-muted mb-0\">").Append(enc.Encode(Subtitle)).Append("</p>");
        }

        var html = new StringBuilder();
        if (hasActions)
        {
            html.Append("<div class=\"d-flex flex-wrap justify-content-between align-items-center gap-3 mb-4\">")
                .Append("<div>").Append(titleInner).Append("</div>")
                .Append("<div class=\"d-flex flex-wrap gap-2\">").Append(actions).Append("</div>")
                .Append("</div>");
        }
        else
        {
            html.Append("<div class=\"mb-4\">").Append(titleInner).Append("</div>");
        }

        output.TagName = null;
        output.Content.SetHtmlContent(html.ToString());
    }

    /// <summary>Mutable holder the nested <see cref="PageHeaderSubtitleTagHelper"/> fills while the child content renders.</summary>
    internal sealed class SubtitleSlot
    {
        public string? Html { get; set; }
    }
}

/// <summary>
/// Markup subtitle for a <c>&lt;page-header&gt;</c>: its content renders in the muted line under
/// the title instead of the plain-text <c>subtitle</c> attribute. Emits nothing itself.
/// </summary>
[HtmlTargetElement("page-header-subtitle", ParentTag = "page-header")]
public class PageHeaderSubtitleTagHelper : TagHelper
{
    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(output);

        var content = (await output.GetChildContentAsync()).GetContent().Trim();
        if (context.Items.TryGetValue(typeof(PageHeaderTagHelper), out var slot) && slot is PageHeaderTagHelper.SubtitleSlot holder)
        {
            holder.Html = content;
        }
        output.SuppressOutput();
    }
}
