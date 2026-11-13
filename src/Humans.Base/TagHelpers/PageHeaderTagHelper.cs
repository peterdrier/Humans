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
///   <page-header>
///       <page-header-title><i class="fa-solid fa-car me-2"></i>Rideshare</page-header-title>
///       <page-header-badge><status-badge value="@Model.Status" /></page-header-badge>
///   </page-header>
///
///   <page-header title="Database" subtitle="..." variant="compact" />
///
/// Always emits exactly one &lt;h1&gt; (so page titles stop drifting to h2) and keeps the
/// badge OUT of the heading text, so screen-reader heading outlines stay clean.
/// The title, subtitle and badge attributes are plain text. Markup for any of them goes in a nested
/// <c>&lt;page-header-title&gt;</c>, <c>&lt;page-header-subtitle&gt;</c> or <c>&lt;page-header-badge&gt;</c>,
/// which wins over the matching attribute; every other child is the actions area.
/// <c>variant="compact"</c> emits the admin-shell <c>page-head</c> band instead.
/// </summary>
[HtmlTargetElement("page-header")]
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

    /// <summary><c>compact</c> renders the admin-shell <c>page-head</c> band; anything else is the standard band.</summary>
    [HtmlAttributeName("variant")]
    public string? Variant { get; set; }

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(output);

        var slots = new Slots();
        context.Items[typeof(PageHeaderTagHelper)] = slots;
        var actions = (await output.GetChildContentAsync()).GetContent().Trim();
        var hasActions = !string.IsNullOrWhiteSpace(actions);
        var enc = HtmlEncoder.Default;
        var compact = string.Equals(Variant, "compact", StringComparison.OrdinalIgnoreCase);

        var badgeHtml = slots.Badge;
        if (string.IsNullOrWhiteSpace(badgeHtml) && !string.IsNullOrWhiteSpace(Badge))
        {
            var badgeClass = string.IsNullOrWhiteSpace(BadgeClass) ? "bg-secondary" : BadgeClass.Trim();
            badgeHtml = $"<span class=\"badge {enc.Encode(badgeClass)}\">{enc.Encode(Badge)}</span>";
        }
        var hasBadge = !string.IsNullOrWhiteSpace(badgeHtml);

        var titleInner = new StringBuilder();
        if (hasBadge)
        {
            titleInner.Append("<div class=\"d-flex align-items-center gap-2\">");
        }
        titleInner.Append(compact ? "<h1 class=\"title\">" : "<h1 class=\"mb-0\">")
                  .Append(string.IsNullOrWhiteSpace(slots.Title) ? enc.Encode(Title ?? string.Empty) : slots.Title)
                  .Append("</h1>");
        if (hasBadge)
        {
            titleInner.Append(badgeHtml).Append("</div>");
        }
        var subClass = compact ? "sub" : "text-muted mb-0";
        if (!string.IsNullOrWhiteSpace(slots.Subtitle))
        {
            titleInner.Append("<div class=\"").Append(subClass).Append("\">").Append(slots.Subtitle).Append("</div>");
        }
        else if (!string.IsNullOrWhiteSpace(Subtitle))
        {
            titleInner.Append("<p class=\"").Append(subClass).Append("\">").Append(enc.Encode(Subtitle)).Append("</p>");
        }

        var html = new StringBuilder();
        if (compact)
        {
            html.Append("<div class=\"page-head\"><div>").Append(titleInner).Append("</div>");
            if (hasActions)
            {
                html.Append("<div class=\"d-flex align-items-center gap-3\">").Append(actions).Append("</div>");
            }
            html.Append("</div>");
        }
        else if (hasActions)
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

    /// <summary>Mutable holder the nested slot tag helpers fill while the child content renders.</summary>
    internal sealed class Slots
    {
        public string? Title { get; set; }
        public string? Subtitle { get; set; }
        public string? Badge { get; set; }

        /// <summary>Renders a slot's content into the enclosing header's holder and suppresses the slot's own output.</summary>
        internal static async Task CaptureAsync(TagHelperContext context, TagHelperOutput output, Action<Slots, string> store)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(output);

            var content = (await output.GetChildContentAsync()).GetContent().Trim();
            if (context.Items.TryGetValue(typeof(PageHeaderTagHelper), out var holder) && holder is Slots slots)
            {
                store(slots, content);
            }
            output.SuppressOutput();
        }
    }
}

/// <summary>
/// Markup title for a <c>&lt;page-header&gt;</c> (an icon, a link, a <c>&lt;small&gt;</c> suffix): its content
/// becomes the &lt;h1&gt; content instead of the plain-text <c>title</c> attribute. Emits nothing itself.
/// </summary>
[HtmlTargetElement("page-header-title", ParentTag = "page-header")]
public class PageHeaderTitleTagHelper : TagHelper
{
    public override Task ProcessAsync(TagHelperContext context, TagHelperOutput output) =>
        PageHeaderTagHelper.Slots.CaptureAsync(context, output, (s, html) => s.Title = html);
}

/// <summary>
/// Badge markup for a <c>&lt;page-header&gt;</c>, typically a <c>&lt;status-badge&gt;</c>: rendered beside the
/// heading in place of the <c>badge</c> attribute. Emits nothing itself.
/// </summary>
[HtmlTargetElement("page-header-badge", ParentTag = "page-header")]
public class PageHeaderBadgeTagHelper : TagHelper
{
    public override Task ProcessAsync(TagHelperContext context, TagHelperOutput output) =>
        PageHeaderTagHelper.Slots.CaptureAsync(context, output, (s, html) => s.Badge = html);
}

/// <summary>
/// Markup subtitle for a <c>&lt;page-header&gt;</c>: its content renders in the muted line under
/// the title instead of the plain-text <c>subtitle</c> attribute. Emits nothing itself.
/// </summary>
[HtmlTargetElement("page-header-subtitle", ParentTag = "page-header")]
public class PageHeaderSubtitleTagHelper : TagHelper
{
    public override Task ProcessAsync(TagHelperContext context, TagHelperOutput output) =>
        PageHeaderTagHelper.Slots.CaptureAsync(context, output, (s, html) => s.Subtitle = html);
}
