using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Humans.Web.TagHelpers;

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
/// Always emits exactly one &lt;h1&gt; (so page titles stop drifting to h2) and keeps the
/// badge OUT of the heading text, so screen-reader heading outlines stay clean.
/// Plain-text title/subtitle/badge only — headers whose subtitle carries markup keep
/// their hand-rolled markup.
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
        if (!string.IsNullOrWhiteSpace(Subtitle))
        {
            titleInner.Append("<p class=\"text-muted mb-0\">").Append(enc.Encode(Subtitle)).Append("</p>");
        }

        var html = new StringBuilder();
        if (hasActions)
        {
            html.Append("<div class=\"d-flex justify-content-between align-items-center gap-3 mb-4\">")
                .Append("<div>").Append(titleInner).Append("</div>")
                .Append("<div class=\"d-flex gap-2 flex-shrink-0\">").Append(actions).Append("</div>")
                .Append("</div>");
        }
        else
        {
            html.Append("<div class=\"mb-4\">").Append(titleInner).Append("</div>");
        }

        output.TagName = null;
        output.Content.SetHtmlContent(html.ToString());
    }
}
