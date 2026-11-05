using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Humans.Base.TagHelpers;

/// <summary>
/// The "nothing here yet" message shown when a list or collection is empty: muted and centered,
/// the same look <c>_Table</c> gives its empty-message row, for blocks that are not tables.
/// The child content is the message, so localized text and markup pass through unchanged.
///
/// Usage:
///   <empty-state>@Localizer["Workgroups_NoActive"]</empty-state>
///   <empty-state class="mb-0 small">@Localizer["Rideshare_NoInterestsYet"]</empty-state>
///
/// Renders a &lt;p&gt; (default paragraph bottom margin); a <c>class</c> attribute is appended
/// for context spacing such as <c>mb-0</c> or <c>mt-3</c>. Never use it for hints or help text,
/// and never inside a table (use <c>EmptyMessage</c> there).
/// </summary>
[HtmlTargetElement("empty-state")]
public class EmptyStateTagHelper : TagHelper
{
    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(output);

        output.TagName = "p";
        output.TagMode = TagMode.StartTagAndEndTag;

        var extra = output.Attributes["class"]?.Value?.ToString();
        output.Attributes.SetAttribute("class",
            string.IsNullOrWhiteSpace(extra) ? "text-muted text-center" : $"text-muted text-center {extra.Trim()}");
    }
}
