using Humans.Base.Hosting;
using Humans.Base.Interfaces;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Humans.Base.TagHelpers;

/// <summary>
/// Drops a link or form that leads into a section switched off for the active event, so no view has to know which
/// section its link lands in. <see cref="SectionRoutes"/> derives the section from the target.
/// </summary>
/// <remarks>
/// Where the link text is data rather than navigation (a team's name in a table), <c>keep-text</c> leaves the text
/// and drops only the anchor. An element around the link that would be left empty takes <c>hide-with-links</c>
/// (<see cref="HideWithLinksTagHelper"/>).
/// </remarks>
[HtmlTargetElement("a", Attributes = "asp-action")]
[HtmlTargetElement("a", Attributes = "asp-controller")]
[HtmlTargetElement("a", Attributes = "href")]
[HtmlTargetElement("form", Attributes = "asp-action")]
[HtmlTargetElement("form", Attributes = "asp-controller")]
[HtmlTargetElement("form", Attributes = "action")]
public sealed class SectionLinkTagHelper : TagHelper
{
    private const string KeepText = "keep-text";

    [HtmlAttributeName("asp-controller")]
    public string? Controller { get; set; }

    [ViewContext]
    [HtmlAttributeNotBound]
    public ViewContext ViewContext { get; set; } = null!;

    /// <summary>
    /// After the framework's anchor and form helpers: they have written <c>href</c>/<c>action</c> by then, and a form
    /// suppressed any earlier would still get their antiforgery field appended.
    /// </summary>
    public override int Order => 1000;

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        var keepText = output.Attributes.RemoveAll(KeepText);
        var switchedOff = ViewContext.HttpContext.SwitchedOffSections();
        if (switchedOff.Count == 0 || output.TagName is null)
            return;

        // From the request, not the constructor: every link in every view is one of these, and a host that renders
        // views without the route map (a section's view tests) has nothing switched off to ask it about.
        if (ViewContext.HttpContext.RequestServices.GetService(typeof(SectionRoutes)) is not SectionRoutes routes)
            return;

        var section = string.IsNullOrEmpty(Controller)
            ? routes.SectionOfUrl(Target(output), ViewContext.HttpContext.Request.PathBase)
            : routes.SectionOfController(Controller);
        var links = context.Items.TryGetValue(typeof(LinkTally), out var tally) ? (LinkTally)tally : null;

        var off = section is not null && switchedOff.Contains(section);

        if (off && keepText)
            output.TagName = null;
        else if (off)
            output.SuppressOutput();

        if (links is null)
            return;
        if (off && !keepText)
            links.Hidden++;
        else
            links.Shown++;
    }

    private static string? Target(TagHelperOutput output) =>
        (output.Attributes["href"] ?? output.Attributes["action"])?.Value?.ToString();

    /// <summary>What the nearest <c>hide-with-links</c> ancestor learns from the links inside it.</summary>
    internal sealed class LinkTally
    {
        public int Shown { get; set; }
        public int Hidden { get; set; }
    }
}

/// <summary>
/// Drops the element once every link inside it was dropped by <see cref="SectionLinkTagHelper"/>: the list item,
/// card or menu row that would otherwise be left empty.
/// </summary>
/// <remarks>
/// Only links in the same view count. A partial or view component rendered inside is a separate page to Razor, so it
/// brings its own wrapper.
/// </remarks>
[HtmlTargetElement("*", Attributes = Attribute)]
public sealed class HideWithLinksTagHelper : TagHelper
{
    private const string Attribute = "hide-with-links";

    private SectionLinkTagHelper.LinkTally? _links;

    [ViewContext]
    [HtmlAttributeNotBound]
    public ViewContext ViewContext { get; set; } = null!;

    public override void Init(TagHelperContext context)
    {
        if (ViewContext.HttpContext.SwitchedOffSections().Count > 0)
            context.Items[typeof(SectionLinkTagHelper.LinkTally)] = _links = new();
    }

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        output.Attributes.RemoveAll(Attribute);
        if (_links is null || output.TagName is null)
            return;

        await output.GetChildContentAsync();
        if (_links is { Hidden: > 0, Shown: 0 })
            output.SuppressOutput();
    }
}
