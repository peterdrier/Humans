using Humans.Base.Extensions;
using Humans.Base.Models.Tables;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.Extensions.Localization;

namespace Humans.Base.TagHelpers;

/// <summary>
/// Renders an enum status as a Bootstrap badge whose colour comes from <see cref="EnumBadgeMap"/> and whose text is
/// the enum's localized display, so views never pick a badge colour themselves.
///
/// Usage:
///   <status-badge value="@Model.Status" />
///   <status-badge value="@Model.Status" class="ms-1" />
///   <status-badge value="@Model.Status" localizer="@Localizer" />
///   <status-badge value="@Model.Period" text="@periodName" />
///
/// <c>class</c> adds spacing/shape classes after the badge ones; <c>text</c> replaces the enum display for views
/// that show non-enum text. <c>localizer</c> is for enums whose <c>Enum_{Type}_{Value}</c> keys live in the
/// section's own resource rather than <c>SharedResource</c> (pass the view's <c>Localizer</c>). A null <c>value</c>
/// renders nothing.
/// </summary>
[HtmlTargetElement("status-badge", Attributes = "value")]
public class StatusBadgeTagHelper(IStringLocalizer<SharedResource> localizer) : TagHelper
{
    /// <summary>The enum value; its <see cref="EnumBadgeMap"/> row picks the colour.</summary>
    [HtmlAttributeName("value")]
    public Enum? Value { get; set; }

    /// <summary>Optional text replacing the enum's localized display (still HTML-encoded).</summary>
    [HtmlAttributeName("text")]
    public string? Text { get; set; }

    /// <summary>Localizer holding the enum's display keys; defaults to <c>SharedResource</c>.</summary>
    [HtmlAttributeName("localizer")]
    public IStringLocalizer? Localizer { get; set; }

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(output);

        if (Value is null)
        {
            output.SuppressOutput();
            return;
        }

        var extra = output.Attributes.TryGetAttribute("class", out var attr) ? attr.Value?.ToString() : null;
        output.Attributes.RemoveAll("class");

        var classes = string.IsNullOrWhiteSpace(extra)
            ? $"badge {EnumBadgeMap.For(Value)}"
            : $"badge {EnumBadgeMap.For(Value)} {extra.Trim()}";

        output.TagName = "span";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", classes);
        output.Content.SetContent(Text ?? (Localizer ?? localizer).EnumDisplay(Value));
    }
}
