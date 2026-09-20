using Ganss.Xss;
using Markdig;

namespace Humans.Base.Extensions;

/// <summary>
/// Canonical Markdown-to-HTML rendering for authored content displayed by Humans.
/// </summary>
public static class SanitizedMarkdownRenderer
{
    private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseSoftlineBreakAsHardlineBreak()
        .Build();

    public static string Render(string? markdown, bool allowImages = true)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        var rendered = Markdown.ToHtml(markdown, MarkdownPipeline);
        var sanitizer = new HtmlSanitizer();

        // Allow task list checkboxes rendered by Markdig's UseTaskLists extension.
        sanitizer.AllowedTags.Add("input");
        sanitizer.AllowedAttributes.Add("type");
        sanitizer.AllowedAttributes.Add("checked");
        sanitizer.AllowedAttributes.Add("disabled");

        // User Markdown never needs inline CSS, and a style attribute's url(...) (e.g.
        // background-image) is not covered by the https-only <img> rule below — it would be a
        // tracking-pixel path the https-only rule doesn't close. Disallow style outright.
        sanitizer.AllowedAttributes.Remove("style");

        if (!allowImages)
        {
            sanitizer.AllowedTags.Remove("img");
        }
        else
        {
            // Images may only reference https:// URLs. This blocks data:/http:/javascript:
            // (and any other scheme) on <img src> and <input type="image" src> (the allowed
            // "input" tag's own image-fetching form, kept for task-list checkboxes) specifically,
            // without touching the broader http/https/mailto allowance FilterUrl leaves for <a href>.
            sanitizer.FilterUrl += (_, e) =>
            {
                var isImageSource = string.Equals(e.Tag.TagName, "IMG", StringComparison.OrdinalIgnoreCase)
                    || (string.Equals(e.Tag.TagName, "INPUT", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(e.Tag.GetAttribute("type"), "image", StringComparison.OrdinalIgnoreCase));

                if (!isImageSource)
                {
                    return;
                }

                if (e.SanitizedUrl is null
                    || !e.SanitizedUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    e.SanitizedUrl = null;
                }
            };
        }

        return sanitizer.Sanitize(rendered);
    }
}
